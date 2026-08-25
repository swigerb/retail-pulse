using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RetailPulse.Api.Agents.Planning;
using RetailPulse.Api.Approval;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Guardrails;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Observability;
using RetailPulse.Api.Persistence;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Approval;
using RetailPulse.Contracts.Guardrails;
using RetailPulse.Contracts.Observability;
using RetailPulse.Contracts.Persistence;
using RetailPulse.Contracts.Routing;
using RetailPulse.Contracts.Tracing;
using RetailPulse.Tests.Fixtures;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Tests.Approval;

/// <summary>
/// End-to-end tests for the non-blocking suspend/resume path (#94 B1/B2/B3).
/// Each test drives:
/// <list type="number">
///   <item><c>PlanOrchestrator.RunAsync</c> — must return Suspended immediately.</item>
///   <item>Reviewer records a decision via <c>IApprovalGate.RespondAsync</c>.</item>
///   <item><c>PlanReviewCompletionService.ResolveAsync</c> — the resume driver.</item>
/// </list>
/// Every test uses a real <see cref="SqliteApprovalGate"/> and a real
/// <see cref="FileSystemJsonCheckpointStore"/>
/// so the persistence + framework-checkpoint surfaces are exercised end-to-end.
/// </summary>
public sealed class PlanReviewCompletionServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _checkpointDir;

    public PlanReviewCompletionServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"prv_completion_{Guid.NewGuid():N}.db");
        _checkpointDir = Path.Combine(Path.GetTempPath(), $"prv_completion_ckpt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_checkpointDir);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
        try { Directory.Delete(_checkpointDir, recursive: true); } catch { }
    }

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ── B1: durable suspend/resume — including simulated restart ────────

    [Fact]
    public async Task Suspend_edit_resume_executes_edited_plan_and_persists_final_response()
    {
        // ── Suspend ──────────────────────────────────────────────────
        (ServiceProvider spBoot1, PlanOrchestrator orch1, InMemoryPlanStore plans1,
            SqliteApprovalGate gate1, ConcurrentQueue<string> invocations1) = BuildHost();

        PlanOrchestrationResult suspend = await orch1.RunAsync(SampleInput(), default);
        suspend.IsSuspended.Should().BeTrue();
        suspend.Status.Should().Be(PlanStatus.AwaitingReview);
        suspend.ReviewRequestId.Should().NotBeNullOrWhiteSpace();

        // ── Simulated restart: dispose the first host entirely ──────
        await spBoot1.DisposeAsync();

        // A second host reads the plan/approval/checkpoint stores from the
        // exact same on-disk locations — proving state is durable.
        (ServiceProvider spBoot2, _, InMemoryPlanStore plans2,
            SqliteApprovalGate gate2, ConcurrentQueue<string> invocations2) =
            BuildHost(reuseDbPath: true, reuseCheckpointDir: true,
                seedPlans: plans1.Snapshot());

        // ── Reviewer records the edit through the fresh gate ────────
        ApprovalRequest row = (await gate2.GetPendingAsync("user-1"))
            .Single(r => r.Context.PlanId == suspend.PlanId);
        var edited = new List<PlanReviewStepDto>
        {
            new() { SpecialistKey = "scorecard", Intent = "scorecard", Action = "EDITED_ACTION" },
        };
        var payload = new PlanReviewResponsePayload
        {
            Kind = PlanReviewKinds.Edit,
            EditedSteps = edited,
        };
        await gate2.RespondAsync(row.RequestId, ApprovalDecision.Modified,
            "trim", JsonSerializer.Serialize(payload, _json));

        // ── Resume drives execution ─────────────────────────────────
        PlanReviewCompletionService completion = spBoot2.GetRequiredService<PlanReviewCompletionService>();
        PlanReviewCompletionResult result = await completion.ResolveAsync(suspend.PlanId, "user-1");

        result.Kind.Should().Be(PlanReviewCompletionKind.Executed);
        result.Reply.Should().NotBeNullOrWhiteSpace();
        invocations2.Should().Contain(s => s.Contains("EDITED_ACTION"),
            "the edited action must be what the specialist sees on the resumed run.");

        PlanDetailDto? plan = await plans2.GetPlanAsync("user-1", suspend.PlanId, default);
        plan.Should().NotBeNull();
        plan.Status.Should().Be(PlanStatus.Completed);
        plan.FailureReason.Should().StartWith("PlanReviewFinalReply::",
            "the resumed final reply is persisted onto the plan record so a later GET returns it.");

        await spBoot2.DisposeAsync();
    }

    // ── B2: clarification round-trip via the executor marker ────────────

    [Fact]
    public async Task Clarification_marker_suspends_plan_and_resume_delivers_answer_as_step_result()
    {
        // Planner emits a [[CLARIFY]] step at index 1 so the executor pauses
        // between step 0 and step 1. Resume with an answer completes step 2.
        (ServiceProvider sp, PlanOrchestrator orch, InMemoryPlanStore plans,
            SqliteApprovalGate gate, ConcurrentQueue<string> invocations) =
            BuildHost(plannerJson: PlannerJsonWithClarify());

        PlanOrchestrationResult suspend = await orch.RunAsync(SampleInput(), default);
        suspend.IsSuspended.Should().BeTrue();

        // Reviewer approves and we resume through review → hits clarification.
        ApprovalRequest reviewRow = (await gate.GetPendingAsync("user-1"))
            .Single(r => r.Context.PlanId == suspend.PlanId);
        var payload = new PlanReviewResponsePayload { Kind = PlanReviewKinds.Approve };
        await gate.RespondAsync(reviewRow.RequestId, ApprovalDecision.Approved,
            "go", JsonSerializer.Serialize(payload, _json));

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();
        PlanReviewCompletionResult reviewResume = await completion.ResolveAsync(suspend.PlanId, "user-1");
        reviewResume.Kind.Should().Be(PlanReviewCompletionKind.SuspendedForClarification,
            "the executor detected the [[CLARIFY]] marker and paused for reviewer input.");

        // Reviewer answers the clarification.
        ApprovalRequest clarRow = (await gate.GetPendingAsync("user-1"))
            .Single(r => r.Context.Kind == ApprovalKind.Clarification
                      && r.Context.PlanId == suspend.PlanId);
        var answer = new PlanClarificationAnswer { Answer = "REVIEWER_ANSWER" };
        await gate.RespondAsync(clarRow.RequestId, ApprovalDecision.Approved,
            "ans", JsonSerializer.Serialize(answer, _json));

        PlanReviewCompletionResult clarResume = await completion.ResolveAsync(suspend.PlanId, "user-1");
        clarResume.Kind.Should().Be(PlanReviewCompletionKind.Executed);
        clarResume.Reply.Should().Contain("REVIEWER_ANSWER",
            "the answer flows into the composed final reply.");

        PlanDetailDto? plan = await plans.GetPlanAsync("user-1", suspend.PlanId, default);
        plan.Should().NotBeNull();
        plan.Status.Should().Be(PlanStatus.Completed);

        await sp.DisposeAsync();
    }

    // ── Preservation: disabled path returns 200-shape immediately ───────

    [Fact]
    public async Task Disabled_review_returns_terminal_result_and_writes_no_approval_row()
    {
        (ServiceProvider sp, PlanOrchestrator orch, _,
            SqliteApprovalGate gate, _) = BuildHost(reviewEnabled: false);

        PlanOrchestrationResult r = await orch.RunAsync(SampleInput(), default);
        r.IsSuspended.Should().BeFalse();
        r.Status.Should().Be(PlanStatus.Completed);
        (await gate.GetPendingAsync("user-1")).Should().BeEmpty();
        (await gate.GetHistoryAsync(50)).Should().BeEmpty(
            "disabled review never touches the approval gate.");

        await sp.DisposeAsync();
    }

    // ── Mid-execution replan surface ([[REPLAN]] marker) ────────────────

    [Fact]
    public async Task Replan_marker_suspends_plan_for_a_new_review_round()
    {
        (ServiceProvider sp, PlanOrchestrator orch, InMemoryPlanStore plans,
            SqliteApprovalGate gate, ConcurrentQueue<string> _) =
            BuildHost(plannerJson: PlannerJsonWithReplan());

        PlanOrchestrationResult suspend = await orch.RunAsync(SampleInput(), default);
        suspend.IsSuspended.Should().BeTrue();

        // Reviewer approves the initial plan so execution starts and hits [[REPLAN]].
        ApprovalRequest firstReview = (await gate.GetPendingAsync("user-1"))
            .Single(r => r.Context.PlanId == suspend.PlanId
                      && r.Context.Kind == ApprovalKind.PlanReview);
        await gate.RespondAsync(firstReview.RequestId, ApprovalDecision.Approved,
            "go", JsonSerializer.Serialize(new PlanReviewResponsePayload
            {
                Kind = PlanReviewKinds.Approve,
            }, _json));

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();
        PlanReviewCompletionResult resume = await completion.ResolveAsync(suspend.PlanId, "user-1");
        resume.Kind.Should().Be(PlanReviewCompletionKind.SuspendedForNextRound,
            "the executor reached the [[REPLAN]] step, opened a NEW plan-review row in the " +
            "same durable table, and the completion service reports the suspension via a " +
            "SuspendedForNextRound outcome so the endpoint layer can broadcast the next-round id.");
        resume.NextRequestId.Should().NotBeNullOrWhiteSpace();

        // A second plan-review row now exists for the same plan — that's the
        // reachable mid-execution revision surface.
        IReadOnlyList<ApprovalRequest> pending = await gate.GetPendingAsync("user-1");
        pending.Any(r => r.Context.PlanId == suspend.PlanId
                      && r.Context.Kind == ApprovalKind.PlanReview
                      && r.Context.Reasoning.Contains("Mid-execution", StringComparison.Ordinal))
            .Should().BeTrue("the [[REPLAN]] marker must open a NEW plan-review row.");

        await sp.DisposeAsync();
    }

    private static string PlannerJsonWithReplan() => /*lang=json,strict*/ @"{ ""steps"": [
        { ""specialist_key"": ""scorecard"", ""intent"": ""scorecard"", ""action"": ""[[REPLAN]] scope too broad"" }
    ] }";

    // ── PII filtering + audit preservation ──────────────────────────────

    [Fact]
    public async Task Resumed_final_response_is_filtered_and_recorded_in_audit_log()
    {
        (ServiceProvider sp, PlanOrchestrator orch, InMemoryPlanStore plans,
            SqliteApprovalGate gate, ConcurrentQueue<string> _) =
            BuildHost(reviewEnabled: true,
                pii: true,   // Explicitly enable PII redaction on the guardrails config.
                specialistReply: "call me at 555-123-4567 anytime");

        PlanOrchestrationResult suspend = await orch.RunAsync(SampleInput(), default);

        ApprovalRequest row = (await gate.GetPendingAsync("user-1"))
            .Single(r => r.Context.PlanId == suspend.PlanId);
        await gate.RespondAsync(row.RequestId, ApprovalDecision.Approved, "go",
            JsonSerializer.Serialize(new PlanReviewResponsePayload
            {
                Kind = PlanReviewKinds.Approve,
            }, _json));

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();
        PlanReviewCompletionResult resume = await completion.ResolveAsync(suspend.PlanId, "user-1");
        resume.Kind.Should().Be(PlanReviewCompletionKind.Executed);
        resume.Reply.Should().NotContain("555-123-4567",
            "PII must be redacted from the final reply on the resumed path.");
        resume.Reply.Should().Contain("REDACTED");

        // Audit log recorded the resumed turn — a plan turn is still an
        // accountable interaction.
        IAuditLog audit = sp.GetRequiredService<IAuditLog>();
        IReadOnlyList<AuditEntry> entries = await audit.QueryAsync(new AuditQuery());
        entries.Any(e => e.Action.Contains("plan.review.resolve", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("resumed plan turns must land in the audit log.");

        await sp.DisposeAsync();
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    private (ServiceProvider Sp, PlanOrchestrator Orchestrator, InMemoryPlanStore PlanStore,
        SqliteApprovalGate Gate, ConcurrentQueue<string> Invocations)
        BuildHost(
            bool reviewEnabled = true,
            string? plannerJson = null,
            bool reuseDbPath = false,
            bool reuseCheckpointDir = false,
            IReadOnlyList<(string Subject, PlanWrite Plan)>? seedPlans = null,
            bool pii = false,
            string? specialistReply = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(TimeProvider.System);

        // Approval + checkpoint (durable on disk between "restart" iterations)
        SqliteApprovalGate gate = new(_dbPath, NullLogger<SqliteApprovalGate>.Instance,
            TimeSpan.FromMinutes(30), TimeProvider.System);
        services.AddSingleton(gate);
        services.AddSingleton<IApprovalGate>(sp => sp.GetRequiredService<SqliteApprovalGate>());

        services.AddSingleton<ICheckpointStore<JsonElement>>(_ =>
                new FileSystemJsonCheckpointStore(
                    new DirectoryInfo(_checkpointDir)));
        services.AddSingleton(sp =>
        {
            ICheckpointStore<JsonElement> store =
                sp.GetRequiredService<ICheckpointStore<JsonElement>>();
            return Microsoft.Agents.AI.Workflows.CheckpointManager.CreateJson(store, customOptions: null);
        });
        services.AddSingleton<PlanReviewCheckpointService>();

        var options = new PlanReviewOptions
        {
            Enabled = reviewEnabled,
            DefaultReviewTimeout = TimeSpan.FromSeconds(30),
            ClarificationTimeout = TimeSpan.FromSeconds(30),
            MaxReplanRounds = 1,
        };
        services.AddSingleton(Options.Create(options));

        services.AddSingleton<PlanClarifier>();
        services.AddSingleton<IPlanClarifier>(sp => sp.GetRequiredService<PlanClarifier>());
        services.AddSingleton<PlanReviewCoordinator>();

        // Plan store
        var plans = new InMemoryPlanStore();
        if (seedPlans is not null)
        {
            foreach ((string sub, PlanWrite pw) in seedPlans)
                plans.RestoreCreate(sub, pw);
        }
        services.AddSingleton<IPlanStore>(plans);

        // Specialists
        var invocations = new ConcurrentQueue<string>();
        ISpecialistAgent scorecard = MakeSpecialist("scorecard", invocations,
            specialistReply ?? "score-reply");
        ISpecialistAgent demand = MakeSpecialist("demand-forecasting", invocations, "demand-reply");
        services.AddSingleton(scorecard);
        services.AddSingleton(demand);

        // Planner (uses the AgentTestFixtures mock chat client)
        var persistOpts = new PlanPersistenceOptions();
        services.AddSingleton(persistOpts);
        services.AddSingleton(new Api.Models.AgentDefinition
        {
            Key = "planner",
            Name = "Plan-First Orchestrator",
            Model = "gpt-test",
            SystemPrompt = "You are the planner.",
            Temperature = 0.1,
        });
        services.AddSingleton(sp => new PlanBuilder(
            AgentTestFixtures.CreateMockChatClient(plannerJson ?? DefaultPlannerJson()),
            sp.GetRequiredService<Api.Models.AgentDefinition>(),
            persistOpts,
            NullLogger<PlanBuilder>.Instance));

        // Cost + trace + executor
        services.AddSingleton<ICostTracker>(new NoOpCostTracker());
        services.AddSingleton<ITraceCollector>(new NoOpTraceCollector());
        services.AddSingleton(sp => new PlanExecutor(
            sp.GetRequiredService<IPlanStore>(),
            sp.GetRequiredService<ICostTracker>(),
            sp.GetRequiredService<ITraceCollector>(),
            sp.GetRequiredService<PlanPersistenceOptions>(),
            NullLogger<PlanExecutor>.Instance,
            sp.GetService<PlanClarifier>(),
            sp.GetService<PlanReviewCoordinator>()));

        services.AddSingleton(sp => new PlanOrchestrator(
            sp.GetRequiredService<PlanBuilder>(),
            sp.GetRequiredService<PlanExecutor>(),
            sp.GetRequiredService<IPlanStore>(),
            sp.GetRequiredService<ICostTracker>(),
            persistOpts,
            NullLogger<PlanOrchestrator>.Instance,
            sp.GetService<PlanReviewCoordinator>(),
            Options.Create(options)));

        // Completion service dependencies
        //
        // GuardrailsMiddleware needs a config + a suspicious-request log. We
        // construct a config where both PII redaction and content-safety
        // output checks are OFF so FilterOutputAsync returns the reply
        // unchanged — the test asserts the flowed-through response text, not
        // the filter's own behavior (that is covered by GuardrailsMiddleware
        // tests).
        services.AddSingleton(new GuardrailsConfig
        {
            PiiDetectionEnabled = pii,
            AutoRedactPii = pii,
            ContentSafety = new ContentSafetyConfig { Enabled = false },
        });
        services.AddSingleton<ISuspiciousRequestLog, InMemorySuspiciousRequestLog>();
        services.AddSingleton<ITenantProvider>(new StubTenantProvider());
        services.AddSingleton<GuardrailsMiddleware>();
        services.AddSingleton<IAuditLog, InMemoryAuditLog>();
        services.AddSingleton(_ => new ConversationExporter(
            Options.Create(new ObservabilityOptions())));

        // SignalR hub stub — return null; the completion service tolerates it.
        services.AddSingleton<IHubContext<TelemetryHub>>(new NullHub());

        services.AddSingleton<PlanReviewCompletionService>();

        ServiceProvider sp = services.BuildServiceProvider();
        return (sp,
            sp.GetRequiredService<PlanOrchestrator>(),
            plans,
            sp.GetRequiredService<SqliteApprovalGate>(),
            invocations);
    }

    private static PlanOrchestrationInput SampleInput()
    {
        ISpecialistAgent scorecard = MakeSpecialist("scorecard", new(), "");
        ISpecialistAgent demand = MakeSpecialist("demand-forecasting", new(), "");
        return new PlanOrchestrationInput
        {
            Request = new ChatRequest("multi", SessionId: "s"),
            Subject = "user-1",
            PrincipalKey = "user-1",
            TenantId = "Contoso",
            Roster = [scorecard, demand],
            SpecialistLookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
            {
                ["scorecard"] = scorecard,
                ["demand-forecasting"] = demand,
            },
            DetectedIntents = ["scorecard", "demand"],
            TraceId = "t",
        };
    }

    private static string DefaultPlannerJson() => /*lang=json,strict*/ @"{ ""steps"": [
        { ""specialist_key"": ""scorecard"", ""intent"": ""scorecard"", ""action"": ""ORIGINAL_ACTION"" },
        { ""specialist_key"": ""demand-forecasting"", ""intent"": ""demand"", ""action"": ""ORIGINAL_DEMAND_ACTION"" }
    ] }";

    private static string PlannerJsonWithClarify() => /*lang=json,strict*/ @"{ ""steps"": [
        { ""specialist_key"": ""scorecard"", ""intent"": ""scorecard"", ""action"": ""step-0"" },
        { ""specialist_key"": ""scorecard"", ""intent"": ""scorecard"", ""action"": ""[[CLARIFY]] Which region?"" },
        { ""specialist_key"": ""demand-forecasting"", ""intent"": ""demand"", ""action"": ""step-2"" }
    ] }";

    private static ISpecialistAgent MakeSpecialist(
        string key, ConcurrentQueue<string> invocations, string reply)
    {
        var m = new Mock<ISpecialistAgent>();
        m.SetupGet(a => a.Key).Returns(key);
        m.SetupGet(a => a.DisplayName).Returns(key);
        m.SetupGet(a => a.Model).Returns("gpt-test");
        m.SetupGet(a => a.SupportedIntents).Returns([key]);
        m.Setup(a => a.HandleAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .Returns((ChatRequest req, CancellationToken _) =>
            {
                invocations.Enqueue(req.Message);
                return Task.FromResult(new ChatResponse(
                    string.IsNullOrEmpty(reply) ? $"{key}-reply" : reply,
                    req.SessionId ?? "s", [], null, 10, new TokenUsage(1, 1, 2)));
            });
        return m.Object;
    }

    // ── Support types (no-op / in-memory doubles) ────────────────────────

    internal sealed class InMemoryPlanStore : IPlanStore
    {
        private readonly Dictionary<string, Dictionary<string, (PlanWrite Create, PlanStatusUpdate? Status, Dictionary<string, PlanStepUpdate> Steps)>> _bySub = new(StringComparer.Ordinal);
        public Task CreatePlanAsync(PlanWrite plan, CancellationToken ct = default)
        {
            if (!_bySub.TryGetValue(plan.Subject, out Dictionary<string, (PlanWrite Create, PlanStatusUpdate? Status, Dictionary<string, PlanStepUpdate> Steps)>? bucket))
            {
                bucket = new(StringComparer.Ordinal);
                _bySub[plan.Subject] = bucket;
            }

            // Seed the initial step rows into the update dictionary so orphan
            // sweeps and step reads see them (issue #149). Each initial step is
            // synthesized as a PlanStepUpdate carrying its initial Status
            // (typically Pending) so downstream reads reflect the actual
            // starting state, not an empty history.
            Dictionary<string, PlanStepUpdate> steps = new(StringComparer.Ordinal);
            foreach (PlanStepWrite step in plan.Steps)
            {
                steps[step.StepId] = new PlanStepUpdate
                {
                    StepId = step.StepId,
                    PlanId = plan.PlanId,
                    Subject = plan.Subject,
                    Status = step.Status,
                };
            }

            bucket[plan.PlanId] = (plan, null, steps);
            return Task.CompletedTask;
        }
        public void RestoreCreate(string subject, PlanWrite plan) => CreatePlanAsync(plan).Wait();
        public IReadOnlyList<(string Subject, PlanWrite Plan)> Snapshot() =>
            [.. _bySub.SelectMany(kv => kv.Value.Values.Select(v => (kv.Key, v.Create)))];
        public Task UpdatePlanStatusAsync(PlanStatusUpdate update, CancellationToken ct = default)
        {
            if (_bySub.TryGetValue(update.Subject, out Dictionary<string, (PlanWrite Create, PlanStatusUpdate? Status, Dictionary<string, PlanStepUpdate> Steps)>? bucket)
                && bucket.TryGetValue(update.PlanId, out (PlanWrite Create, PlanStatusUpdate? Status, Dictionary<string, PlanStepUpdate> Steps) row))
            {
                bucket[update.PlanId] = (row.Create, update, row.Steps);

                // Mirror SqlitePlanStore's terminal-status orphan sweep
                // (issue #149): any Pending/Running step row is transitioned
                // to Skipped so the test double honors the same contract the
                // production store enforces.
                if (IsTerminalPlanStatus(update.Status))
                {
                    foreach (string stepId in row.Steps.Keys.ToArray())
                    {
                        PlanStepUpdate existing = row.Steps[stepId];
                        if (existing.Status is PlanStepStatus.Pending or PlanStepStatus.Running)
                        {
                            row.Steps[stepId] = existing with
                            {
                                Status = PlanStepStatus.Skipped,
                                CompletedAt = existing.CompletedAt ?? update.UpdatedAt,
                            };
                        }
                    }
                }
            }
            return Task.CompletedTask;
        }

        private static bool IsTerminalPlanStatus(string status) =>
            status is PlanStatus.Completed
                or PlanStatus.Failed
                or PlanStatus.Cancelled
                or PlanStatus.Unusable;
        public Task UpdateStepAsync(PlanStepUpdate update, CancellationToken ct = default)
        {
            if (_bySub.TryGetValue(update.Subject, out Dictionary<string, (PlanWrite Create, PlanStatusUpdate? Status, Dictionary<string, PlanStepUpdate> Steps)>? bucket)
                && bucket.TryGetValue(update.PlanId, out (PlanWrite Create, PlanStatusUpdate? Status, Dictionary<string, PlanStepUpdate> Steps) row))
            {
                row.Steps[update.StepId] = update;
            }
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<PlanSummaryDto>> ListPlansForSubjectAsync(string subject, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PlanSummaryDto>>([]);
        public Task<PlanDetailDto?> GetPlanAsync(string subject, string planId, CancellationToken ct = default)
        {
            if (!_bySub.TryGetValue(subject, out Dictionary<string, (PlanWrite Create, PlanStatusUpdate? Status, Dictionary<string, PlanStepUpdate> Steps)>? bucket)
                || !bucket.TryGetValue(planId, out (PlanWrite Create, PlanStatusUpdate? Status, Dictionary<string, PlanStepUpdate> Steps) row))
            {
                return Task.FromResult<PlanDetailDto?>(null);
            }

            string status = row.Status?.Status ?? row.Create.Status;
            string? failureReason = row.Status?.FailureReason;

            // Project step updates back into PlanStepRecordDto so callers
            // (including regression tests for issue #149) can observe the
            // step-lifecycle state through the standard read path. The
            // originating PlanStepWrite carries the specialist metadata; the
            // latest PlanStepUpdate carries the lifecycle transition.
            var writesByStepId = row.Create.Steps.ToDictionary(s => s.StepId, StringComparer.Ordinal);
            var stepRecords = new List<PlanStepRecordDto>();
            foreach ((string stepId, PlanStepUpdate stepUpdate) in row.Steps)
            {
                writesByStepId.TryGetValue(stepId, out PlanStepWrite? initialWrite);
                stepRecords.Add(new PlanStepRecordDto(
                    StepId: stepId,
                    PlanId: row.Create.PlanId,
                    StepIndex: initialWrite?.StepIndex ?? 0,
                    SpecialistKey: initialWrite?.SpecialistKey ?? string.Empty,
                    Intent: initialWrite?.Intent ?? string.Empty,
                    Action: initialWrite?.Action ?? string.Empty,
                    Status: stepUpdate.Status,
                    Result: stepUpdate.Result,
                    Error: stepUpdate.Error,
                    InputTokens: stepUpdate.InputTokens,
                    OutputTokens: stepUpdate.OutputTokens,
                    TotalTokens: stepUpdate.TotalTokens,
                    DurationMs: stepUpdate.DurationMs,
                    StartedAt: stepUpdate.StartedAt,
                    CompletedAt: stepUpdate.CompletedAt));
            }
            stepRecords = [.. stepRecords.OrderBy(s => s.StepIndex).ThenBy(s => s.StepId, StringComparer.Ordinal)];

            return Task.FromResult<PlanDetailDto?>(new PlanDetailDto(
                PlanId: row.Create.PlanId,
                SessionId: row.Create.SessionId,
                TenantId: row.Create.TenantId,
                Request: row.Create.Request,
                Status: status,
                DetectedIntents: row.Create.DetectedIntents,
                FailureReason: failureReason,
                TotalInputTokens: null, TotalOutputTokens: null, TotalTokens: null,
                TotalDurationMs: null,
                CreatedAt: row.Create.CreatedAt,
                UpdatedAt: row.Status?.UpdatedAt ?? row.Create.CreatedAt,
                Steps: stepRecords));
        }
        public Task<bool> DeletePlanAsync(string subject, string planId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<PlanCleanupResult> PurgeExpiredAsync(DateTimeOffset olderThan, CancellationToken ct = default)
            => Task.FromResult(new PlanCleanupResult(0, 0));

        /// <summary>
        /// Test-only accessor exposing the last recorded step transition for a
        /// given plan/step id. Returns <see langword="null"/> when the plan or
        /// step id has never been touched. Used by state-integrity regression
        /// tests to prove that resume paths update in-flight step rows out of
        /// their <c>Pending</c> holding state instead of leaving them stranded.
        /// </summary>
        internal PlanStepUpdate? GetLastStepUpdate(string subject, string planId, string stepId)
        {
            return _bySub.TryGetValue(subject, out Dictionary<string, (PlanWrite Create, PlanStatusUpdate? Status, Dictionary<string, PlanStepUpdate> Steps)>? bucket)
                && bucket.TryGetValue(planId, out (PlanWrite Create, PlanStatusUpdate? Status, Dictionary<string, PlanStepUpdate> Steps) row)
                && row.Steps.TryGetValue(stepId, out PlanStepUpdate? step)
                ? step
                : null;
        }

        /// <summary>
        /// Test-only accessor exposing the last plan-level status update.
        /// Returns <see langword="null"/> when the plan was created but never
        /// transitioned. Used by state-integrity regression tests to prove
        /// that plans never strand in <c>Running</c> after the resume path
        /// takes ownership.
        /// </summary>
        internal PlanStatusUpdate? GetLastStatusUpdate(string subject, string planId)
        {
            return _bySub.TryGetValue(subject, out Dictionary<string, (PlanWrite Create, PlanStatusUpdate? Status, Dictionary<string, PlanStepUpdate> Steps)>? bucket)
                && bucket.TryGetValue(planId, out (PlanWrite Create, PlanStatusUpdate? Status, Dictionary<string, PlanStepUpdate> Steps) row)
                ? row.Status
                : null;
        }
    }

    private sealed class NoOpTraceCollector : ITraceCollector
    {
        public void CaptureSpan(TraceSpan span) { }
        public IReadOnlyList<TraceSpan>? GetSpans(string traceId) => null;
        public TraceSummary? GetSummary(string traceId) => null;
        public IReadOnlyList<TraceSummary> GetRecentTraces(int count = 20) => [];
        public StructuredTraceSummary? GetStructuredSummary(string traceId) => null;
        public IReadOnlyList<ToolUsageStat> GetToolStats(DateTimeOffset since, int top = 10) => [];
        public int TraceCount => 0;
        public int Capacity => 100;
    }

    private sealed class NoOpCostTracker : ICostTracker
    {
        public Task TrackUsageAsync(UsageEvent usage, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CostSummary> GetSummaryAsync(CostPeriod period, CancellationToken ct = default)
            => Task.FromResult(new CostSummary(0, 0, 0, period));
        public Task<IReadOnlyList<AgentCostBreakdown>> GetByAgentAsync(CostPeriod period, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentCostBreakdown>>([]);
        public Task<CostTrend> GetTrendAsync(int days = 7, CancellationToken ct = default)
            => Task.FromResult(new CostTrend([]));
    }

    private sealed class StubTenantProvider : ITenantProvider
    {
        public TenantConfiguration GetTenant() => new()
        {
            Company = "Contoso",
            Industry = "test",
        };
    }

    private sealed class NullHub : IHubContext<TelemetryHub>
    {
        public IHubClients Clients { get; } = new NullClients();
        public IGroupManager Groups { get; } = new NullGroupManager();

        private sealed class NullClients : IHubClients
        {
            public IClientProxy All => NullProxy.Instance;
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => NullProxy.Instance;
            public IClientProxy Client(string connectionId) => NullProxy.Instance;
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => NullProxy.Instance;
            public IClientProxy Group(string groupName) => NullProxy.Instance;
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => NullProxy.Instance;
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => NullProxy.Instance;
            public IClientProxy User(string userId) => NullProxy.Instance;
            public IClientProxy Users(IReadOnlyList<string> userIds) => NullProxy.Instance;
        }
        private sealed class NullGroupManager : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
        private sealed class NullProxy : IClientProxy
        {
            public static readonly NullProxy Instance = new();
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}

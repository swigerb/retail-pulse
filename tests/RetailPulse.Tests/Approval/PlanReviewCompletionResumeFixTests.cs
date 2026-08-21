using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.AspNetCore.SignalR;
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
/// Regression coverage for the follow-up fixes on top of #136/#137:
/// clarification-resume coordinate model, cumulative charts/results across
/// repeated clarifications, subject/session-scoped broadcast isolation, and
/// concurrent decision / clarification execution claim.
/// </summary>
public sealed class PlanReviewCompletionResumeFixTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _checkpointDir;

    public PlanReviewCompletionResumeFixTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"prv_resume_fix_{Guid.NewGuid():N}.db");
        _checkpointDir = Path.Combine(Path.GetTempPath(), $"prv_resume_fix_ckpt_{Guid.NewGuid():N}");
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

    // ── Coordinate model correctness ────────────────────────────────────

    /// <summary>
    /// Plan: [specialist-A, clarification-at-step-1, specialist-B]. Before
    /// the fix, the resume path treated <c>PausedAtStepIndex</c> as an index
    /// into the SLICED <c>Steps</c> list, so any non-first clarification
    /// selected the wrong paused step and produced an empty remaining
    /// slice — silently skipping specialist-B entirely. After the fix, the
    /// paused step resolves as <c>Steps[0]</c> and downstream runs as
    /// <c>Steps.Skip(1)</c>, so specialist-B invokes exactly once with the
    /// full accumulated context on the resumed plan.
    /// </summary>
    [Fact]
    public async Task Clarification_at_middle_step_runs_downstream_specialist_on_resume()
    {
        (ServiceProvider sp, PlanOrchestrator orch, _,
            SqliteApprovalGate gate, ConcurrentQueue<string> invocations, _) =
            BuildHost(plannerJson: PlannerJsonWithClarifyInMiddle());

        PlanOrchestrationResult suspend = await orch.RunAsync(SampleInput(), default);
        suspend.IsSuspended.Should().BeTrue();

        // Reviewer approves the initial plan; execution starts and hits [[CLARIFY]] at step 1.
        ApprovalRequest reviewRow = (await gate.GetPendingAsync("user-1"))
            .Single(r => r.Context.PlanId == suspend.PlanId
                      && r.Context.Kind == ApprovalKind.PlanReview);
        await gate.RespondAsync(reviewRow.RequestId, ApprovalDecision.Approved, "go",
            JsonSerializer.Serialize(new PlanReviewResponsePayload { Kind = PlanReviewKinds.Approve }, _json));

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();
        PlanReviewCompletionResult r1 = await completion.ResolveAsync(suspend.PlanId, "user-1");
        r1.Kind.Should().Be(PlanReviewCompletionKind.SuspendedForClarification);

        // Reviewer answers the clarification.
        ApprovalRequest clarRow = (await gate.GetPendingAsync("user-1"))
            .Single(r => r.Context.Kind == ApprovalKind.Clarification
                      && r.Context.PlanId == suspend.PlanId);
        await gate.RespondAsync(clarRow.RequestId, ApprovalDecision.Approved, "ans",
            JsonSerializer.Serialize(new PlanClarificationAnswer { Answer = "north" }, _json));

        PlanReviewCompletionResult r2 = await completion.ResolveAsync(suspend.PlanId, "user-1");
        r2.Kind.Should().Be(PlanReviewCompletionKind.Executed);

        // Downstream demand-forecasting specialist MUST have been invoked
        // exactly once with its planned action. Before the fix, the buggy
        // coordinate model would resolve remaining=[] and skip this step.
        invocations.Count(s => s.Contains("DOWNSTREAM_DEMAND", StringComparison.Ordinal))
            .Should().Be(1,
                "the resume path must run every step after the paused clarification exactly once.");

        // Chart/reply order stable — scorecard runs first (index 0), then
        // the answer substitutes for the paused step, then demand runs.
        r2.Reply.Should().Contain("SCORECARD_REPLY");
        r2.Reply.Should().Contain("north");
        r2.Reply.Should().Contain("DEMAND_REPLY");
        int idxScore = r2.Reply.IndexOf("SCORECARD_REPLY", StringComparison.Ordinal);
        int idxAnswer = r2.Reply.IndexOf("north", StringComparison.Ordinal);
        int idxDemand = r2.Reply.IndexOf("DEMAND_REPLY", StringComparison.Ordinal);
        idxScore.Should().BeLessThan(idxAnswer);
        idxAnswer.Should().BeLessThan(idxDemand);

        await sp.DisposeAsync();
    }

    /// <summary>
    /// Plan with TWO clarifications: [A, CLARIFY, B, CLARIFY, C]. The
    /// second clarification runs on a resumed plan, so the executor is
    /// seeded with the pre-round transcript through
    /// <see cref="PlanExecutionRequest.PriorAccumulatedResults"/>. Every
    /// pre-round chart and specialist result must survive both pauses and
    /// appear on the final broadcast; the final downstream specialist C
    /// must execute exactly once.
    /// </summary>
    [Fact]
    public async Task Repeated_clarifications_preserve_every_prior_chart_and_specialist_result()
    {
        ChartSpec chartScore = MakeChart("bar", "score");
        ChartSpec chartDemand = MakeChart("line", "demand");

        (ServiceProvider sp, PlanOrchestrator orch, _,
            SqliteApprovalGate gate, ConcurrentQueue<string> invocations, CapturingHub hub) =
            BuildHost(
                plannerJson: PlannerJsonWithTwoClarifications(),
                specialistChartsByKey: new Dictionary<string, IReadOnlyList<ChartSpec>?>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["scorecard"] = [chartScore],
                    ["demand-forecasting"] = [chartDemand],
                    ["competitive-intel"] = null,
                });

        // Suspend for initial review.
        PlanOrchestrationResult suspend = await orch.RunAsync(SampleInput(), default);
        suspend.IsSuspended.Should().BeTrue();

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();

        // Approve → hit the FIRST [[CLARIFY]] at step 1.
        await ApprovePlanReviewAsync(gate, suspend.PlanId);
        PlanReviewCompletionResult r1 = await completion.ResolveAsync(suspend.PlanId, "user-1");
        r1.Kind.Should().Be(PlanReviewCompletionKind.SuspendedForClarification);

        // Answer first clarification → executor runs step B, then hits SECOND [[CLARIFY]] at step 3.
        await AnswerClarificationAsync(gate, suspend.PlanId, "first-answer");
        PlanReviewCompletionResult r2 = await completion.ResolveAsync(suspend.PlanId, "user-1");
        r2.Kind.Should().Be(PlanReviewCompletionKind.SuspendedForClarification,
            "the resumed plan must be able to CLARIFY again — the resume path threads " +
            "PriorAccumulatedResults so the second checkpoint sees the FULL transcript.");

        // Answer second clarification → executor runs final downstream C.
        await AnswerClarificationAsync(gate, suspend.PlanId, "second-answer");
        PlanReviewCompletionResult r3 = await completion.ResolveAsync(suspend.PlanId, "user-1");
        r3.Kind.Should().Be(PlanReviewCompletionKind.Executed);

        // Every specialist runs exactly once.
        invocations.Count(s => s.Contains("ACTION_A", StringComparison.Ordinal)).Should().Be(1);
        invocations.Count(s => s.Contains("ACTION_B", StringComparison.Ordinal)).Should().Be(1);
        invocations.Count(s => s.Contains("ACTION_C", StringComparison.Ordinal)).Should().Be(1);

        // Both answers survive on the final reply.
        r3.Reply.Should().Contain("first-answer");
        r3.Reply.Should().Contain("second-answer");

        // Every pre-clarification specialist chart survives — dropping any
        // would be the "AccumulatedResults reset across clarifications"
        // defect.
        r3.Charts.Should().Contain(c => c.Type == "bar",
            "the scorecard chart emitted before ANY clarification must reach the final broadcast.");
        r3.Charts.Should().Contain(c => c.Type == "line",
            "the demand chart emitted between clarifications must also reach the final broadcast.");

        // Final broadcast payload carries the same charts.
        IReadOnlyList<ChartSpec>? broadcastCharts = hub.LastFinalCharts;
        broadcastCharts.Should().NotBeNull();
        broadcastCharts.Should().Contain(c => c.Type == "bar");
        broadcastCharts.Should().Contain(c => c.Type == "line");

        await sp.DisposeAsync();
    }

    // ── Broadcast isolation ─────────────────────────────────────────────

    /// <summary>
    /// Every subject/session-specific broadcast MUST route through
    /// <see cref="IHubClients.Group"/> keyed on the persisted session id;
    /// <see cref="IHubClients.All"/> must never be accessed on the resume
    /// path. The recording hub double distinguishes All / Group / User (not
    /// the same proxy) so a regression that flips back to Clients.All fails
    /// this assertion loudly.
    /// </summary>
    [Fact]
    public async Task Resume_broadcasts_route_through_session_group_and_never_Clients_All()
    {
        (ServiceProvider sp, PlanOrchestrator orch, _,
            SqliteApprovalGate gate, _, CapturingHub hub) = BuildHost();

        PlanOrchestrationResult suspend = await orch.RunAsync(SampleInput(sessionId: "session-alpha"), default);
        suspend.IsSuspended.Should().BeTrue();

        await ApprovePlanReviewAsync(gate, suspend.PlanId);

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();
        PlanReviewCompletionResult resume = await completion.ResolveAsync(suspend.PlanId, "user-1");
        resume.Kind.Should().Be(PlanReviewCompletionKind.Executed);

        hub.AllAccessCount.Should().Be(0,
            "Clients.All must never be selected on the plan-review resume path — every event " +
            "is subject-scoped and must go through Clients.Group(sessionId).");
        hub.UserAccessCount.Should().Be(0,
            "Clients.User is not the isolation seam used here; only Clients.Group is authorized.");
        hub.GroupsUsed.Should().Contain("session-alpha",
            "the plan_final_response broadcast must target the persisted session group.");
        hub.MethodsSentToGroup("session-alpha").Should().Contain("plan_final_response");

        await sp.DisposeAsync();
    }

    /// <summary>
    /// Two plans owned by two distinct subjects/sessions must broadcast to
    /// their own session groups only — a broadcast targeting subject A's
    /// session must NEVER surface in subject B's group and vice versa. Any
    /// regression that routes to Clients.All would show both subjects
    /// receiving both broadcasts.
    /// </summary>
    [Fact]
    public async Task Multi_subject_broadcasts_are_isolated_to_each_subjects_session_group()
    {
        (ServiceProvider sp, PlanOrchestrator orch, _,
            SqliteApprovalGate gate, _, CapturingHub hub) = BuildHost();

        PlanOrchestrationResult a = await orch.RunAsync(SampleInput(subject: "user-A", sessionId: "sess-A"), default);
        PlanOrchestrationResult b = await orch.RunAsync(SampleInput(subject: "user-B", sessionId: "sess-B"), default);
        a.IsSuspended.Should().BeTrue();
        b.IsSuspended.Should().BeTrue();

        await ApprovePlanReviewAsync(gate, a.PlanId, subject: "user-A");
        await ApprovePlanReviewAsync(gate, b.PlanId, subject: "user-B");

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();
        await completion.ResolveAsync(a.PlanId, "user-A");
        await completion.ResolveAsync(b.PlanId, "user-B");

        hub.AllAccessCount.Should().Be(0);
        hub.GroupsUsed.Should().Contain("sess-A");
        hub.GroupsUsed.Should().Contain("sess-B");

        // Neither session must have received the other subject's payload.
        hub.PayloadsSentToGroup("sess-A")
            .Should().OnlyContain(o => PayloadPlanId(o) == a.PlanId,
                "user-A's session group must never receive user-B's plan payloads.");
        hub.PayloadsSentToGroup("sess-B")
            .Should().OnlyContain(o => PayloadPlanId(o) == b.PlanId,
                "user-B's session group must never receive user-A's plan payloads.");

        await sp.DisposeAsync();
    }

    /// <summary>
    /// Fail-closed policy: when the checkpoint has no session id, the
    /// completion service MUST skip the broadcast entirely rather than
    /// falling back to Clients.All. The reply is still persisted onto the
    /// plan record so the HTTP GET surface returns it correctly.
    /// </summary>
    [Fact]
    public async Task Missing_session_id_fails_closed_and_never_broadcasts()
    {
        (ServiceProvider sp, PlanOrchestrator orch, _,
            SqliteApprovalGate gate, _, CapturingHub hub) = BuildHost();

        // Suspend a plan with a null session id — SampleInput lets us omit it.
        PlanOrchestrationResult suspend = await orch.RunAsync(SampleInput(sessionId: null), default);
        suspend.IsSuspended.Should().BeTrue();

        await ApprovePlanReviewAsync(gate, suspend.PlanId);

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();
        PlanReviewCompletionResult resume = await completion.ResolveAsync(suspend.PlanId, "user-1");
        resume.Kind.Should().Be(PlanReviewCompletionKind.Executed);

        hub.AllAccessCount.Should().Be(0);
        hub.GroupAccessCount.Should().Be(0,
            "no session id means the resume path fails closed — nothing is broadcast.");

        await sp.DisposeAsync();
    }

    // ── Concurrent execution claim ──────────────────────────────────────

    /// <summary>
    /// Two concurrent <c>ResolveAsync</c> callers observing the same
    /// persisted-approved approval row MUST NOT both run the specialists.
    /// The <see cref="IPlanStore.TryTransitionStatusAsync"/> claim
    /// collapses the race to exactly one execution; the losing caller
    /// returns <c>NoOp</c>.
    /// </summary>
    [Fact]
    public async Task Concurrent_resolve_on_approved_plan_runs_specialists_exactly_once()
    {
        (ServiceProvider sp, PlanOrchestrator orch, _,
            SqliteApprovalGate gate, ConcurrentQueue<string> invocations, CapturingHub hub) = BuildHost();

        PlanOrchestrationResult suspend = await orch.RunAsync(SampleInput(), default);
        suspend.IsSuspended.Should().BeTrue();

        await ApprovePlanReviewAsync(gate, suspend.PlanId);

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();

        // Fire two concurrent resolves; only one should execute the plan.
        Task<PlanReviewCompletionResult> t1 = completion.ResolveAsync(suspend.PlanId, "user-1");
        Task<PlanReviewCompletionResult> t2 = completion.ResolveAsync(suspend.PlanId, "user-1");
        PlanReviewCompletionResult[] outcomes = await Task.WhenAll(t1, t2);

        outcomes.Count(o => o.Kind == PlanReviewCompletionKind.Executed).Should().Be(1,
            "exactly one concurrent resume driver should run the executor.");
        outcomes.Count(o => o.Kind == PlanReviewCompletionKind.NoOp).Should().Be(1,
            "the losing caller must NoOp — not execute the plan a second time.");

        // Each planned step should have invoked its specialist exactly once.
        invocations.Count(s => s.Contains("ORIGINAL_ACTION", StringComparison.Ordinal)).Should().Be(1);
        invocations.Count(s => s.Contains("ORIGINAL_DEMAND_ACTION", StringComparison.Ordinal)).Should().Be(1);

        // Exactly one final broadcast to the session group.
        hub.MethodsSentToGroup("s").Count(m => m == "plan_final_response")
            .Should().Be(1,
                "the SignalR surface must see exactly one plan_final_response, not one per losing caller.");

        await sp.DisposeAsync();
    }

    /// <summary>
    /// Concurrent clarification resumes: two callers hit
    /// <c>ResolveAsync</c> after the reviewer answers; only one should
    /// drive the downstream execution. The persisted answer/kind wins —
    /// the losing caller's local state can never override the row.
    /// </summary>
    [Fact]
    public async Task Concurrent_resolve_on_answered_clarification_runs_downstream_exactly_once()
    {
        (ServiceProvider sp, PlanOrchestrator orch, _,
            SqliteApprovalGate gate, ConcurrentQueue<string> invocations, CapturingHub hub) =
            BuildHost(plannerJson: PlannerJsonWithClarifyInMiddle());

        PlanOrchestrationResult suspend = await orch.RunAsync(SampleInput(), default);
        suspend.IsSuspended.Should().BeTrue();

        await ApprovePlanReviewAsync(gate, suspend.PlanId);

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();
        PlanReviewCompletionResult r1 = await completion.ResolveAsync(suspend.PlanId, "user-1");
        r1.Kind.Should().Be(PlanReviewCompletionKind.SuspendedForClarification);

        await AnswerClarificationAsync(gate, suspend.PlanId, "the-persisted-answer");

        // Two concurrent resolves after the reviewer answered.
        Task<PlanReviewCompletionResult> t1 = completion.ResolveAsync(suspend.PlanId, "user-1");
        Task<PlanReviewCompletionResult> t2 = completion.ResolveAsync(suspend.PlanId, "user-1");
        PlanReviewCompletionResult[] outcomes = await Task.WhenAll(t1, t2);

        outcomes.Count(o => o.Kind == PlanReviewCompletionKind.Executed).Should().Be(1);

        // Downstream must run exactly once — not zero times, not twice.
        invocations.Count(s => s.Contains("DOWNSTREAM_DEMAND", StringComparison.Ordinal)).Should().Be(1);

        // The persisted answer wins on every executed reply / broadcast.
        PlanReviewCompletionResult executed = outcomes.Single(o => o.Kind == PlanReviewCompletionKind.Executed);
        executed.Reply.Should().Contain("the-persisted-answer");

        hub.MethodsSentToGroup("s").Count(m => m == "plan_final_response")
            .Should().Be(1);

        await sp.DisposeAsync();
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    private static ChartSpec MakeChart(string type, string title) => new()
    {
        Type = type,
        Title = title,
        Data =
        [
            new ChartSeries
            {
                Legend = "series",
                Values = [new ChartDataPoint { X = "Q1", Y = 1 }],
            },
        ],
    };

    private static async Task ApprovePlanReviewAsync(
        SqliteApprovalGate gate, string planId, string subject = "user-1")
    {
        ApprovalRequest row = (await gate.GetPendingAsync(subject))
            .Single(r => r.Context.PlanId == planId
                      && r.Context.Kind == ApprovalKind.PlanReview);
        await gate.RespondAsync(row.RequestId, ApprovalDecision.Approved, "go",
            JsonSerializer.Serialize(new PlanReviewResponsePayload { Kind = PlanReviewKinds.Approve }, _json));
    }

    private static async Task AnswerClarificationAsync(
        SqliteApprovalGate gate, string planId, string answer, string subject = "user-1")
    {
        ApprovalRequest row = (await gate.GetPendingAsync(subject))
            .Single(r => r.Context.PlanId == planId
                      && r.Context.Kind == ApprovalKind.Clarification);
        await gate.RespondAsync(row.RequestId, ApprovalDecision.Approved, "ans",
            JsonSerializer.Serialize(new PlanClarificationAnswer { Answer = answer }, _json));
    }

    private static string PayloadPlanId(object payload)
    {
        System.Reflection.PropertyInfo? prop = payload.GetType().GetProperty("planId");
        return prop?.GetValue(payload) as string ?? string.Empty;
    }

    private (ServiceProvider Sp, PlanOrchestrator Orchestrator,
        PlanReviewCompletionServiceTests.InMemoryPlanStore PlanStore, SqliteApprovalGate Gate,
        ConcurrentQueue<string> Invocations, CapturingHub Hub)
        BuildHost(
            string? plannerJson = null,
            IReadOnlyDictionary<string, IReadOnlyList<ChartSpec>?>? specialistChartsByKey = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(TimeProvider.System);

        SqliteApprovalGate gate = new(_dbPath, NullLogger<SqliteApprovalGate>.Instance,
            TimeSpan.FromMinutes(30), TimeProvider.System);
        services.AddSingleton(gate);
        services.AddSingleton<IApprovalGate>(sp => sp.GetRequiredService<SqliteApprovalGate>());

        services.AddSingleton<ICheckpointStore<JsonElement>>(_ =>
                new FileSystemJsonCheckpointStore(new DirectoryInfo(_checkpointDir)));
        services.AddSingleton(sp =>
        {
            ICheckpointStore<JsonElement> store = sp.GetRequiredService<ICheckpointStore<JsonElement>>();
            return Microsoft.Agents.AI.Workflows.CheckpointManager.CreateJson(store, customOptions: null);
        });
        services.AddSingleton<PlanReviewCheckpointService>();

        var options = new PlanReviewOptions
        {
            Enabled = true,
            DefaultReviewTimeout = TimeSpan.FromSeconds(30),
            ClarificationTimeout = TimeSpan.FromSeconds(30),
            MaxReplanRounds = 1,
        };
        services.AddSingleton(Options.Create(options));

        services.AddSingleton<PlanClarifier>();
        services.AddSingleton<IPlanClarifier>(sp => sp.GetRequiredService<PlanClarifier>());
        services.AddSingleton<PlanReviewCoordinator>();

        var plans = new PlanReviewCompletionServiceTests.InMemoryPlanStore();
        services.AddSingleton<IPlanStore>(plans);

        var invocations = new ConcurrentQueue<string>();

        IReadOnlyList<ChartSpec>? scoreCharts = specialistChartsByKey?.GetValueOrDefault("scorecard");
        IReadOnlyList<ChartSpec>? demandCharts = specialistChartsByKey?.GetValueOrDefault("demand-forecasting");
        IReadOnlyList<ChartSpec>? competitiveCharts = specialistChartsByKey?.GetValueOrDefault("competitive-intel");

        ISpecialistAgent scorecard = MakeSpecialist("scorecard", invocations, "SCORECARD_REPLY", scoreCharts);
        ISpecialistAgent demand = MakeSpecialist("demand-forecasting", invocations, "DEMAND_REPLY", demandCharts);
        ISpecialistAgent competitive = MakeSpecialist("competitive-intel", invocations, "COMPETITIVE_REPLY", competitiveCharts);
        services.AddSingleton(scorecard);
        services.AddSingleton(demand);
        services.AddSingleton(competitive);

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

        services.AddSingleton(new GuardrailsConfig
        {
            PiiDetectionEnabled = false,
            AutoRedactPii = false,
            ContentSafety = new ContentSafetyConfig { Enabled = false },
        });
        services.AddSingleton<ISuspiciousRequestLog, InMemorySuspiciousRequestLog>();
        services.AddSingleton<ITenantProvider>(new StubTenantProvider());
        services.AddSingleton<GuardrailsMiddleware>();
        services.AddSingleton<IAuditLog, InMemoryAuditLog>();
        services.AddSingleton(_ => new ConversationExporter(
            Options.Create(new ObservabilityOptions())));

        var hub = new CapturingHub();
        services.AddSingleton<IHubContext<TelemetryHub>>(hub);

        services.AddSingleton<PlanReviewCompletionService>();

        ServiceProvider sp2 = services.BuildServiceProvider();
        return (sp2,
            sp2.GetRequiredService<PlanOrchestrator>(),
            plans,
            sp2.GetRequiredService<SqliteApprovalGate>(),
            invocations,
            hub);
    }

    private static PlanOrchestrationInput SampleInput(
        string subject = "user-1", string? sessionId = "s")
    {
        ISpecialistAgent scorecard = MakeSpecialist("scorecard", new(), "", null);
        ISpecialistAgent demand = MakeSpecialist("demand-forecasting", new(), "", null);
        ISpecialistAgent competitive = MakeSpecialist("competitive-intel", new(), "", null);
        return new PlanOrchestrationInput
        {
            Request = new ChatRequest("multi", SessionId: sessionId),
            Subject = subject,
            PrincipalKey = subject,
            TenantId = "Contoso",
            Roster = [scorecard, demand, competitive],
            SpecialistLookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
            {
                ["scorecard"] = scorecard,
                ["demand-forecasting"] = demand,
                ["competitive-intel"] = competitive,
            },
            DetectedIntents = ["scorecard", "demand", "competitive"],
            TraceId = "t",
        };
    }

    private static string DefaultPlannerJson() => /*lang=json,strict*/ @"{ ""steps"": [
        { ""specialist_key"": ""scorecard"", ""intent"": ""scorecard"", ""action"": ""ORIGINAL_ACTION"" },
        { ""specialist_key"": ""demand-forecasting"", ""intent"": ""demand"", ""action"": ""ORIGINAL_DEMAND_ACTION"" }
    ] }";

    /// <summary>Plan: [scorecard, CLARIFY at step 1, demand]. The paused
    /// step is at absolute index 1 — the OLD coordinate model would
    /// mis-select downstream on resume.</summary>
    private static string PlannerJsonWithClarifyInMiddle() => /*lang=json,strict*/ @"{ ""steps"": [
        { ""specialist_key"": ""scorecard"", ""intent"": ""scorecard"", ""action"": ""ORIGINAL_ACTION"" },
        { ""specialist_key"": ""scorecard"", ""intent"": ""scorecard"", ""action"": ""[[CLARIFY]] Which region?"" },
        { ""specialist_key"": ""demand-forecasting"", ""intent"": ""demand"", ""action"": ""DOWNSTREAM_DEMAND"" }
    ] }";

    /// <summary>Plan: [A, CLARIFY, B, CLARIFY, C] — exercises repeated
    /// clarifications and cumulative AccumulatedResults preservation.</summary>
    private static string PlannerJsonWithTwoClarifications() => /*lang=json,strict*/ @"{ ""steps"": [
        { ""specialist_key"": ""scorecard"", ""intent"": ""scorecard"", ""action"": ""ACTION_A"" },
        { ""specialist_key"": ""scorecard"", ""intent"": ""scorecard"", ""action"": ""[[CLARIFY]] First?"" },
        { ""specialist_key"": ""demand-forecasting"", ""intent"": ""demand"", ""action"": ""ACTION_B"" },
        { ""specialist_key"": ""demand-forecasting"", ""intent"": ""demand"", ""action"": ""[[CLARIFY]] Second?"" },
        { ""specialist_key"": ""competitive-intel"", ""intent"": ""competitive"", ""action"": ""ACTION_C"" }
    ] }";

    private static ISpecialistAgent MakeSpecialist(
        string key, ConcurrentQueue<string> invocations, string reply, IReadOnlyList<ChartSpec>? charts)
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
                List<ChartSpec>? chartList = charts is null ? null : [.. charts];
                return Task.FromResult(new ChatResponse(
                    string.IsNullOrEmpty(reply) ? $"{key}-reply" : reply,
                    req.SessionId ?? "s", [], chartList, 10, new TokenUsage(1, 1, 2)));
            });
        return m.Object;
    }

    // ── Support doubles ──────────────────────────────────────────────────

    /// <summary>
    /// Records HOW hub client selection happened — All vs Group vs User
    /// return distinct proxies so a regression from Clients.Group back to
    /// Clients.All is observable. Group name / method / payload triples
    /// are captured so assertions can verify subject isolation and that
    /// each broadcast lands in exactly the expected group.
    /// </summary>
    private sealed class CapturingHub : IHubContext<TelemetryHub>
    {
        private readonly Recorder _recorder = new();

        public int AllAccessCount => _recorder.AllAccessCount;
        public int GroupAccessCount => _recorder.GroupAccessCount;
        public int UserAccessCount => _recorder.UserAccessCount;

        public IReadOnlyCollection<string> GroupsUsed => _recorder.GroupsUsed;

        public IReadOnlyList<string> MethodsSentToGroup(string groupName)
            => _recorder.MethodsForGroup(groupName);

        public IReadOnlyList<object> PayloadsSentToGroup(string groupName)
            => _recorder.PayloadsForGroup(groupName);

        public IReadOnlyList<ChartSpec>? LastFinalCharts => _recorder.LastFinalCharts;
        public object? LastFinalPayload => _recorder.LastFinalPayload;

        public IHubClients Clients { get; }
        public IGroupManager Groups { get; } = new StubGroups();

        public CapturingHub()
        {
            Clients = new RecordingClients(_recorder);
        }

        private sealed class Recorder
        {
            public int AllAccessCount { get; private set; }
            public int GroupAccessCount { get; private set; }
            public int UserAccessCount { get; private set; }
            private readonly HashSet<string> _groupsUsed = new(StringComparer.Ordinal);
            private readonly Dictionary<string, List<(string Method, object Payload)>> _groupEvents = new(StringComparer.Ordinal);

            public IReadOnlyList<ChartSpec>? LastFinalCharts { get; private set; }
            public object? LastFinalPayload { get; private set; }

            public IReadOnlyCollection<string> GroupsUsed => _groupsUsed;

            public void RecordAll() => AllAccessCount++;
            public void RecordUser() => UserAccessCount++;

            public void RecordGroup(string name)
            {
                GroupAccessCount++;
                _groupsUsed.Add(name);
            }

            public void CaptureGroupEvent(string groupName, string method, object payload)
            {
                if (!_groupEvents.TryGetValue(groupName, out List<(string Method, object Payload)>? bucket))
                {
                    bucket = [];
                    _groupEvents[groupName] = bucket;
                }
                bucket.Add((method, payload));
                if (string.Equals(method, "plan_final_response", StringComparison.Ordinal))
                {
                    LastFinalPayload = payload;
                    System.Reflection.PropertyInfo? p = payload.GetType().GetProperty("charts");
                    LastFinalCharts = p?.GetValue(payload) as IReadOnlyList<ChartSpec>;
                }
            }

            public IReadOnlyList<string> MethodsForGroup(string groupName)
                => _groupEvents.TryGetValue(groupName, out List<(string Method, object Payload)>? bucket)
                    ? [.. bucket.Select(t => t.Method)]
                    : [];

            public IReadOnlyList<object> PayloadsForGroup(string groupName)
                => _groupEvents.TryGetValue(groupName, out List<(string Method, object Payload)>? bucket)
                    ? [.. bucket.Select(t => t.Payload)]
                    : [];
        }

        private sealed class RecordingClients(Recorder recorder) : IHubClients
        {
            public IClientProxy All
            {
                get { recorder.RecordAll(); return new NoOpProxy(); }
            }
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds)
            {
                recorder.RecordAll();
                return new NoOpProxy();
            }
            public IClientProxy Client(string connectionId) => new NoOpProxy();
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new NoOpProxy();
            public IClientProxy Group(string groupName)
            {
                recorder.RecordGroup(groupName);
                return new GroupProxy(recorder, groupName);
            }
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds)
            {
                recorder.RecordGroup(groupName);
                return new GroupProxy(recorder, groupName);
            }
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => new NoOpProxy();
            public IClientProxy User(string userId)
            {
                recorder.RecordUser();
                return new NoOpProxy();
            }
            public IClientProxy Users(IReadOnlyList<string> userIds)
            {
                recorder.RecordUser();
                return new NoOpProxy();
            }
        }

        private sealed class GroupProxy(Recorder recorder, string groupName) : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            {
                if (args.Length > 0 && args[0] is not null)
                {
                    recorder.CaptureGroupEvent(groupName, method, args[0]!);
                }
                return Task.CompletedTask;
            }
        }

        private sealed class NoOpProxy : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
                => Task.CompletedTask;
        }

        private sealed class StubGroups : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
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
}

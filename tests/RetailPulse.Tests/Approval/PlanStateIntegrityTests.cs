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
/// Regression coverage for issue #145 — the plan-path state-integrity findings
/// that the PR #144 review identified against <c>main</c>. Each test is
/// designed to fail on the pre-fix code so a future regression is caught
/// deterministically; the assertions are worded to explain the underlying
/// finding.
/// </summary>
public sealed class PlanStateIntegrityTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _checkpointDir;

    public PlanStateIntegrityTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"prv_state_{Guid.NewGuid():N}.db");
        _checkpointDir = Path.Combine(Path.GetTempPath(), $"prv_state_ckpt_{Guid.NewGuid():N}");
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

    // ── Finding 1: clarification resume + persisted row lifecycle ────────

    /// <summary>
    /// Reproduces finding 1(b): the paused step's row is written as
    /// <see cref="PlanStepStatus.Pending"/> at suspension time but never
    /// transitioned to <see cref="PlanStepStatus.Completed"/> once the
    /// reviewer's answer replaces its transcript on resume. Without the fix
    /// the plan row settles honestly but individual step history reads still
    /// advertise an answered clarification as pending, which is dishonest
    /// and breaks downstream reconciliation.
    /// </summary>
    [Fact]
    public async Task Clarification_resume_transitions_paused_step_row_out_of_pending()
    {
        (ServiceProvider sp, PlanOrchestrator orch,
            PlanReviewCompletionServiceTests.InMemoryPlanStore plans,
            SqliteApprovalGate gate, _) =
            BuildHost(plannerJson: PlannerJsonClarifyAtStepOne());

        PlanOrchestrationResult suspend = await orch.RunAsync(SampleInput(), default);
        suspend.IsSuspended.Should().BeTrue();

        // Approve the initial plan → executor runs step 0, hits [[CLARIFY]] at step 1.
        ApprovalRequest reviewRow = (await gate.GetPendingAsync("user-1"))
            .Single(r => r.Context.PlanId == suspend.PlanId
                      && r.Context.Kind == ApprovalKind.PlanReview);
        await gate.RespondAsync(reviewRow.RequestId, ApprovalDecision.Approved, "go",
            JsonSerializer.Serialize(new PlanReviewResponsePayload
            {
                Kind = PlanReviewKinds.Approve,
            }, _json));

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();
        PlanReviewCompletionResult reviewResume = await completion.ResolveAsync(suspend.PlanId, "user-1");
        reviewResume.Kind.Should().Be(PlanReviewCompletionKind.SuspendedForClarification);

        // Reviewer answers → resume drives execution across the pause.
        ApprovalRequest clarRow = (await gate.GetPendingAsync("user-1"))
            .Single(r => r.Context.Kind == ApprovalKind.Clarification
                      && r.Context.PlanId == suspend.PlanId);
        await gate.RespondAsync(clarRow.RequestId, ApprovalDecision.Approved, "ans",
            JsonSerializer.Serialize(new PlanClarificationAnswer { Answer = "REVIEWER_ANSWER" }, _json));

        PlanReviewCompletionResult clarResume = await completion.ResolveAsync(suspend.PlanId, "user-1");
        clarResume.Kind.Should().Be(PlanReviewCompletionKind.Executed);

        // Plan-review + clarification double-suspends materialize step rows
        // under the review-round naming scheme `{planId}-r{round}-s{index}`
        // (see PlanReviewCompletionService.ExecuteApprovedPlanAsync). The
        // executor's SuspendForClarificationAsync then writes the paused row
        // under that same id with Status = Pending. On answer resume it MUST
        // transition to Completed so plan-detail reads don't advertise an
        // answered clarification as still pending.
        string pausedStepId = $"{suspend.PlanId}-r0-s1";
        PlanStepUpdate? pausedUpdate = plans.GetLastStepUpdate("user-1", suspend.PlanId, pausedStepId);
        pausedUpdate.Should().NotBeNull(
            "the paused clarification step's row must exist under the initial-plan step id.");
        pausedUpdate.Status.Should().Be(PlanStepStatus.Completed,
            "an answered clarification MUST transition its persisted row out of Pending — otherwise " +
            "plan-detail reads keep reporting the answered step as awaiting reviewer input (finding 1b).");
        pausedUpdate.Result.Should().Contain("REVIEWER_ANSWER",
            "the reviewer's answer should live on the same step row that was paused, not only inside a " +
            "transient in-memory transcript on the resume path.");

        await sp.DisposeAsync();
    }

    /// <summary>
    /// Reproduces finding 1(a): on a clarification resume, the executor's
    /// emitted <c>plan.step_index</c> tag is the local 0-based index into
    /// the remaining slice, while the persisted step id is cumulative
    /// (offset + i). Consumers keying on the tag see step 0 for what the
    /// store keys as step 2 and can no longer join telemetry against
    /// persisted records.
    /// </summary>
    [Fact]
    public async Task Clarification_resume_step_span_index_matches_cumulative_persisted_index()
    {
        var traces = new RecordingTraceCollector();
        (ServiceProvider sp, PlanOrchestrator orch,
            PlanReviewCompletionServiceTests.InMemoryPlanStore _,
            SqliteApprovalGate gate, _) =
            BuildHost(plannerJson: PlannerJsonClarifyAtStepOne(), tracer: traces);

        PlanOrchestrationResult suspend = await orch.RunAsync(SampleInput(), default);
        suspend.IsSuspended.Should().BeTrue();

        ApprovalRequest reviewRow = (await gate.GetPendingAsync("user-1"))
            .Single(r => r.Context.Kind == ApprovalKind.PlanReview
                      && r.Context.PlanId == suspend.PlanId);
        await gate.RespondAsync(reviewRow.RequestId, ApprovalDecision.Approved, "go",
            JsonSerializer.Serialize(new PlanReviewResponsePayload
            {
                Kind = PlanReviewKinds.Approve,
            }, _json));

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();
        _ = await completion.ResolveAsync(suspend.PlanId, "user-1");

        ApprovalRequest clarRow = (await gate.GetPendingAsync("user-1"))
            .Single(r => r.Context.Kind == ApprovalKind.Clarification
                      && r.Context.PlanId == suspend.PlanId);
        await gate.RespondAsync(clarRow.RequestId, ApprovalDecision.Approved, "ans",
            JsonSerializer.Serialize(new PlanClarificationAnswer { Answer = "REVIEWER_ANSWER" }, _json));

        traces.Spans.Clear();
        _ = await completion.ResolveAsync(suspend.PlanId, "user-1");

        // The clarification is at step index 1; the demand-forecasting step
        // that runs on resume is the original plan's step 2, so its emitted
        // span index must report 2 — not 0 (the local 0-based index of the
        // post-pause slice).
        TraceSpan[] resumeSteps = [.. traces.Spans
            .Where(s => s.Tags is { } tags
                     && tags.TryGetValue("span.type", out string? t)
                     && string.Equals(t, "plan_step", StringComparison.Ordinal))];
        resumeSteps.Should().NotBeEmpty(
            "the resume executor must emit a plan_step span for the specialist that runs after the pause.");
        TraceSpan resumeStep = resumeSteps.Single(s => s.Tags!["plan.step_specialist"] == "demand-forecasting");
        resumeStep.Tags!["plan.step_index"].Should().Be("2",
            "the emitted step index must be the cumulative index the plan store keys on, not the local " +
            "0-based index of the post-pause slice — otherwise span consumers cannot join telemetry to " +
            "persisted step rows (finding 1a).");

        await sp.DisposeAsync();
    }

    // ── Finding 2: mid-plan [[REPLAN]] preserves the completed prefix ────

    /// <summary>
    /// Reproduces finding 2: a mid-plan <c>[[REPLAN]]</c> marker suspends
    /// the plan without carrying the results of steps that already completed
    /// before the marker. On the reviewer's approval of the replanned round,
    /// the resume path silently drops the completed prefix — its reply text
    /// and its charts. This is the highest-severity finding because it
    /// deletes user-visible data (specialist replies and chart specs) after
    /// the plan already succeeded on those steps.
    /// </summary>
    [Fact]
    public async Task Replan_marker_preserves_completed_prefix_results_and_charts_across_resume()
    {
        ChartSpec chartA = MakeChart("bar", "scorecard-A");
        (ServiceProvider sp, PlanOrchestrator orch,
            PlanReviewCompletionServiceTests.InMemoryPlanStore _,
            SqliteApprovalGate gate, CapturingHub hub) =
            BuildHost(plannerJson: PlannerJsonReplanAtStepOne(),
                specialistChartsByKey: new Dictionary<string, IReadOnlyList<ChartSpec>?>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["scorecard"] = [chartA],
                    ["demand-forecasting"] = null,
                },
                specialistReplyByKey: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["scorecard"] = "SCORECARD_PREFIX_RESULT",
                    ["demand-forecasting"] = "DEMAND_REPLAN_RESULT",
                });

        // Approve the initial plan so the executor runs step 0 (scorecard,
        // completes with chartA + prefix result) and then hits [[REPLAN]] at
        // step 1. SuspendForMidPlanReviewAsync opens a new plan-review row
        // that lists the post-replan step (demand-forecasting).
        PlanOrchestrationResult suspend = await orch.RunAsync(SampleInput(), default);
        suspend.IsSuspended.Should().BeTrue();

        ApprovalRequest firstReview = (await gate.GetPendingAsync("user-1"))
            .Single(r => r.Context.PlanId == suspend.PlanId
                      && r.Context.Kind == ApprovalKind.PlanReview);
        await gate.RespondAsync(firstReview.RequestId, ApprovalDecision.Approved, "go",
            JsonSerializer.Serialize(new PlanReviewResponsePayload
            {
                Kind = PlanReviewKinds.Approve,
            }, _json));

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();
        PlanReviewCompletionResult replanResume = await completion.ResolveAsync(suspend.PlanId, "user-1");
        replanResume.Kind.Should().Be(PlanReviewCompletionKind.SuspendedForNextRound,
            "the [[REPLAN]] marker must open a new plan-review row for the replanned round.");

        // Approve the replanned round → the resume runs demand-forecasting.
        ApprovalRequest replanReview = (await gate.GetPendingAsync("user-1"))
            .Single(r => r.Context.PlanId == suspend.PlanId
                      && r.Context.Kind == ApprovalKind.PlanReview
                      && r.RequestId != firstReview.RequestId);
        await gate.RespondAsync(replanReview.RequestId, ApprovalDecision.Approved, "go",
            JsonSerializer.Serialize(new PlanReviewResponsePayload
            {
                Kind = PlanReviewKinds.Approve,
            }, _json));

        PlanReviewCompletionResult final = await completion.ResolveAsync(suspend.PlanId, "user-1");
        final.Kind.Should().Be(PlanReviewCompletionKind.Executed);

        // The pre-replan specialist's result and its chart must survive the
        // review round-trip. Otherwise the reviewer sees only the
        // post-replan step's output on the final broadcast — the completed
        // prefix silently vanishes.
        final.Reply.Should().Contain("SCORECARD_PREFIX_RESULT",
            "the completed prefix (steps before [[REPLAN]]) must be preserved in the resumed final reply " +
            "— dropping it deletes user-visible specialist output that already succeeded (finding 2).");
        final.Reply.Should().Contain("DEMAND_REPLAN_RESULT",
            "the post-replan step's result must also be included alongside the preserved prefix.");
        final.Charts.Should().Contain(c => c.Type == "bar",
            "charts emitted by pre-[[REPLAN]] steps must NOT be discarded when the reviewer approves the " +
            "replanned plan — the fast path preserves them and the resume path must too (finding 2).");

        IReadOnlyList<ChartSpec>? broadcastCharts = ExtractCharts(hub.LastFinalPayload);
        broadcastCharts.Should().NotBeNull(
            "the final SignalR broadcast must include the surviving prefix charts.");
        broadcastCharts.Should().Contain(c => c.Type == "bar");

        await sp.DisposeAsync();
    }

    // ── Finding 3: executor faults after Running claim never strand plan ─

    /// <summary>
    /// Reproduces finding 3: after the completion service transitions a
    /// plan from <see cref="PlanStatus.AwaitingReview"/> to
    /// <see cref="PlanStatus.Running"/> and then the executor fails (or the
    /// process crashes) before its own finalizer records a terminal status,
    /// the plan is stranded in <c>Running</c> forever. The restart-recovery
    /// service only scans <c>Awaiting*</c> plans, so stranded rows never
    /// self-heal. The fix must guarantee that any exception raised inside
    /// the executor path is captured and the plan row transitions to a
    /// terminal state rather than remaining <c>Running</c>.
    /// </summary>
    [Fact]
    public async Task Executor_failure_after_Running_transition_does_not_strand_plan_in_Running()
    {
        // Store that succeeds on the initial CreatePlanAsync + the
        // "AwaitingReview → Running" flip but throws on ANY subsequent
        // plan-status update — i.e. the exact transition the executor's
        // finally block writes. Simulates a transient DB fault between the
        // Running claim and the terminal write.
        var plans = new ExplodingPlanStore();

        (ServiceProvider sp, PlanOrchestrator orch, SqliteApprovalGate gate, _) =
            BuildHostWithStore(plans);

        PlanOrchestrationResult suspend = await orch.RunAsync(SampleInput(), default);
        suspend.IsSuspended.Should().BeTrue();

        ApprovalRequest row = (await gate.GetPendingAsync("user-1"))
            .Single(r => r.Context.PlanId == suspend.PlanId
                      && r.Context.Kind == ApprovalKind.PlanReview);
        await gate.RespondAsync(row.RequestId, ApprovalDecision.Approved, "go",
            JsonSerializer.Serialize(new PlanReviewResponsePayload
            {
                Kind = PlanReviewKinds.Approve,
            }, _json));

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();

        // The executor will throw during its finalize write. The completion
        // service must catch that and settle the plan row into a terminal
        // state — either Failed or another terminal — instead of leaving it
        // stranded in Running. It MUST NOT rethrow to callers, because the
        // decision endpoint dispatches this fire-and-forget.
        plans.ExplodeOnTerminalTransitions = true;
        Func<Task> act = async () => await completion.ResolveAsync(suspend.PlanId, "user-1");
        await act.Should().NotThrowAsync(
            "the endpoint dispatches ResolveAsync fire-and-forget; the completion service must contain " +
            "the fault and finalize the plan row itself, otherwise stranded Running plans never recover.");

        plans.LastStatusFor(suspend.PlanId, "user-1")
            .Should().NotBe(PlanStatus.Running,
                "a plan MUST NOT remain in Running after the resume path takes ownership: if the executor " +
                "faults, the completion service must finalize the row so the restart-recovery service's " +
                "Awaiting-only sweep isn't the sole hope of recovery (finding 3).");

        await sp.DisposeAsync();
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    // ── Finding 4 (issue #149): terminal plan state leaves no orphan Pending step rows ─

    /// <summary>
    /// Reproduces issue #149: <see cref="PlanOrchestrator.SuspendForReviewAsync"/>
    /// writes initial step rows under <c>{planId}-s{i}</c> as
    /// <see cref="PlanStepStatus.Pending"/>. When the reviewer approves and
    /// execution succeeds via <see cref="PlanReviewCompletionService.ExecuteApprovedPlanAsync"/>,
    /// execution writes a parallel <c>{planId}-r{round}-s{i}</c> set — the
    /// original rows would otherwise linger as Pending forever. The contract
    /// this test pins: after ANY terminal plan status is written, no step row
    /// for that plan may remain Pending or Running. Enforced inside
    /// <see cref="IPlanStore.UpdatePlanStatusAsync"/> so every caller (executor
    /// finally block, completion-service finalisers, restart recovery)
    /// inherits the invariant without having to remember to sweep.
    /// </summary>
    [Fact]
    public async Task Review_approved_completed_plan_leaves_no_orphan_pending_step_rows()
    {
        (ServiceProvider sp, PlanOrchestrator orch,
            PlanReviewCompletionServiceTests.InMemoryPlanStore plans,
            SqliteApprovalGate gate, _) = BuildHost();

        PlanOrchestrationResult suspend = await orch.RunAsync(SampleInput(), default);
        suspend.IsSuspended.Should().BeTrue();

        // Confirm the initial `{planId}-s{i}` rows exist as Pending before
        // approval — this is the state that would otherwise be orphaned.
        PlanDetailDto? beforeApproval = await plans.GetPlanAsync("user-1", suspend.PlanId);
        beforeApproval.Should().NotBeNull();
        beforeApproval.Steps.Should().HaveCount(2);
        beforeApproval.Steps.Should().OnlyContain(s => s.Status == PlanStepStatus.Pending,
            "the initial planner-proposed step rows are Pending at review-open time.");
        beforeApproval.Steps.Select(s => s.StepId).Should().BeEquivalentTo(
            [$"{suspend.PlanId}-s0", $"{suspend.PlanId}-s1"],
            "the initial rows use the '{{planId}}-s{{i}}' naming scheme from SuspendForReviewAsync.");

        ApprovalRequest reviewRow = (await gate.GetPendingAsync("user-1"))
            .Single(r => r.Context.PlanId == suspend.PlanId
                      && r.Context.Kind == ApprovalKind.PlanReview);
        await gate.RespondAsync(reviewRow.RequestId, ApprovalDecision.Approved, "go",
            JsonSerializer.Serialize(new PlanReviewResponsePayload
            {
                Kind = PlanReviewKinds.Approve,
            }, _json));

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();
        PlanReviewCompletionResult result = await completion.ResolveAsync(suspend.PlanId, "user-1");
        result.Kind.Should().Be(PlanReviewCompletionKind.Executed);

        PlanDetailDto? after = await plans.GetPlanAsync("user-1", suspend.PlanId);
        after.Should().NotBeNull();
        after.Status.Should().Be(PlanStatus.Completed,
            "the plan must reach a Completed terminal state on approved-and-successful execution.");
        after.Steps.Should().NotContain(s => s.Status == PlanStepStatus.Pending,
            "no step row may remain Pending after the plan reaches a terminal state (issue #149). " +
            "Without the terminal-transition orphan sweep in UpdatePlanStatusAsync, the initial " +
            "'{{planId}}-s{{i}}' rows written by SuspendForReviewAsync would linger as Pending forever.");
        after.Steps.Should().NotContain(s => s.Status == PlanStepStatus.Running,
            "no step row may remain Running after the plan reaches a terminal state (issue #149).");

        // The initial `{planId}-s{i}` rows specifically must be Skipped (not
        // still-Pending, not still-Running) — this is the exact orphan class
        // the issue calls out.
        IEnumerable<PlanStepRecordDto> initialRows = after.Steps
            .Where(s => s.StepId == $"{suspend.PlanId}-s0" || s.StepId == $"{suspend.PlanId}-s1");
        initialRows.Should().OnlyContain(s => s.Status == PlanStepStatus.Skipped,
            "the pre-execution planner-proposal rows must be transitioned to Skipped when execution " +
            "supersedes them with round-scoped rows.");

        await sp.DisposeAsync();
    }

    /// <summary>
    /// Issue #149, terminal Failed variant: any terminal transition to
    /// <see cref="PlanStatus.Failed"/> must sweep orphan Pending/Running step
    /// rows to Skipped. Verified directly at the store contract so the
    /// invariant holds no matter which failure path (planner unavailable,
    /// replan exhausted, executor fault) wrote the transition.
    /// </summary>
    [Fact]
    public async Task Review_rejected_failed_plan_leaves_no_orphan_pending_step_rows()
    {
        var plans = new PlanReviewCompletionServiceTests.InMemoryPlanStore();
        await SeedSuspendedPlanAsync(plans, "plan-fail-149");

        await plans.UpdatePlanStatusAsync(new PlanStatusUpdate
        {
            PlanId = "plan-fail-149",
            Subject = "user-1",
            Status = PlanStatus.Failed,
            FailureReason = "PlanReviewRejected: reviewer rejected",
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        PlanDetailDto? after = await plans.GetPlanAsync("user-1", "plan-fail-149");
        after.Should().NotBeNull();
        after.Status.Should().Be(PlanStatus.Failed);
        after.Steps.Should().NotContain(s => s.Status == PlanStepStatus.Pending,
            "no step row may remain Pending after the plan reaches Failed (issue #149).");
        after.Steps.Should().OnlyContain(s => s.Status == PlanStepStatus.Skipped,
            "on a rejected/failed terminal plan every initial step row must be Skipped — none of them ever ran.");
    }

    /// <summary>
    /// Issue #149, terminal Cancelled variant: a plan that reaches
    /// <see cref="PlanStatus.Cancelled"/> (via caller-initiated cancellation,
    /// timeout, or any other cancel path) must not leave step rows in
    /// Pending or Running. Verified directly at the plan-store level so the
    /// invariant holds no matter which caller writes the terminal transition.
    /// </summary>
    [Fact]
    public async Task Cancelled_plan_leaves_no_orphan_pending_step_rows()
    {
        var plans = new PlanReviewCompletionServiceTests.InMemoryPlanStore();
        await SeedSuspendedPlanAsync(plans, "plan-cancel-149");

        // Simulate a Running claim before cancellation (mirrors the real
        // flow: AwaitingReview → Running → Cancelled if the executor's
        // OperationCanceledException catch fires).
        await plans.UpdateStepAsync(new PlanStepUpdate
        {
            StepId = "plan-cancel-149-r0-s0",
            PlanId = "plan-cancel-149",
            Subject = "user-1",
            Status = PlanStepStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
        });

        await plans.UpdatePlanStatusAsync(new PlanStatusUpdate
        {
            PlanId = "plan-cancel-149",
            Subject = "user-1",
            Status = PlanStatus.Cancelled,
            FailureReason = "cancelled by caller",
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        PlanDetailDto? after = await plans.GetPlanAsync("user-1", "plan-cancel-149");
        after.Should().NotBeNull();
        after.Status.Should().Be(PlanStatus.Cancelled);
        after.Steps.Should().NotContain(s => s.Status == PlanStepStatus.Pending,
            "no step row may remain Pending after the plan reaches Cancelled (issue #149).");
        after.Steps.Should().NotContain(s => s.Status == PlanStepStatus.Running,
            "no step row may remain Running after the plan reaches Cancelled (issue #149) — " +
            "otherwise a stranded Running row survives the cancel.");
        after.Steps.Should().OnlyContain(s => s.Status == PlanStepStatus.Skipped,
            "every non-terminal step must be swept to Skipped on the Cancelled transition.");
    }

    private static async Task SeedSuspendedPlanAsync(
        PlanReviewCompletionServiceTests.InMemoryPlanStore plans, string planId)
    {
        await plans.CreatePlanAsync(new PlanWrite
        {
            PlanId = planId,
            Subject = "user-1",
            SessionId = "sess",
            TenantId = "Contoso",
            Request = "multi",
            DetectedIntents = ["scorecard", "demand"],
            Status = PlanStatus.AwaitingReview,
            CreatedAt = DateTimeOffset.UtcNow,
            Steps =
            [
                new PlanStepWrite
                {
                    StepId = $"{planId}-s0",
                    StepIndex = 0,
                    SpecialistKey = "scorecard",
                    Intent = "scorecard",
                    Action = "act-0",
                    Status = PlanStepStatus.Pending,
                },
                new PlanStepWrite
                {
                    StepId = $"{planId}-s1",
                    StepIndex = 1,
                    SpecialistKey = "demand",
                    Intent = "demand",
                    Action = "act-1",
                    Status = PlanStepStatus.Pending,
                },
            ],
        });
    }

    private static ChartSpec MakeChart(string type, string title) => new()
    {
        Type = type,
        Title = title,
        Data =
        [
            new ChartSeries
            {
                Legend = "series-a",
                Values = [new ChartDataPoint { X = "Q1", Y = 1 }],
            },
        ],
    };

    private static IReadOnlyList<ChartSpec>? ExtractCharts(object? payload)
    {
        if (payload is null) return null;
        Type t = payload.GetType();
        System.Reflection.PropertyInfo? prop = t.GetProperty("charts")
            ?? t.GetProperty("Charts");
        return prop is null ? null : prop.GetValue(payload) as IReadOnlyList<ChartSpec>;
    }

    private (ServiceProvider Sp, PlanOrchestrator Orchestrator,
        PlanReviewCompletionServiceTests.InMemoryPlanStore PlanStore,
        SqliteApprovalGate Gate, CapturingHub Hub)
        BuildHost(
            string? plannerJson = null,
            IReadOnlyDictionary<string, IReadOnlyList<ChartSpec>?>? specialistChartsByKey = null,
            IReadOnlyDictionary<string, string>? specialistReplyByKey = null,
            RecordingTraceCollector? tracer = null)
    {
        var plans = new PlanReviewCompletionServiceTests.InMemoryPlanStore();
        ServiceProvider sp = BuildServices(plans, plannerJson, specialistChartsByKey,
            specialistReplyByKey, tracer, out CapturingHub hub);
        return (sp,
            sp.GetRequiredService<PlanOrchestrator>(),
            plans,
            sp.GetRequiredService<SqliteApprovalGate>(),
            hub);
    }

    private (ServiceProvider Sp, PlanOrchestrator Orchestrator, SqliteApprovalGate Gate, CapturingHub Hub)
        BuildHostWithStore(IPlanStore store)
    {
        ServiceProvider sp = BuildServices(store, plannerJson: null,
            specialistChartsByKey: null, specialistReplyByKey: null,
            tracer: null, out CapturingHub hub);
        return (sp,
            sp.GetRequiredService<PlanOrchestrator>(),
            sp.GetRequiredService<SqliteApprovalGate>(),
            hub);
    }

    private ServiceProvider BuildServices(
        IPlanStore planStore,
        string? plannerJson,
        IReadOnlyDictionary<string, IReadOnlyList<ChartSpec>?>? specialistChartsByKey,
        IReadOnlyDictionary<string, string>? specialistReplyByKey,
        RecordingTraceCollector? tracer,
        out CapturingHub hub)
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

        services.AddSingleton(planStore);

        IReadOnlyList<ChartSpec>? scorecardCharts = specialistChartsByKey?.GetValueOrDefault("scorecard");
        IReadOnlyList<ChartSpec>? demandCharts = specialistChartsByKey?.GetValueOrDefault("demand-forecasting");
        string scorecardReply = specialistReplyByKey?.GetValueOrDefault("scorecard") ?? "score-reply";
        string demandReply = specialistReplyByKey?.GetValueOrDefault("demand-forecasting") ?? "demand-reply";
        ISpecialistAgent scorecard = MakeSpecialist("scorecard", scorecardReply, scorecardCharts);
        ISpecialistAgent demand = MakeSpecialist("demand-forecasting", demandReply, demandCharts);
        services.AddSingleton(scorecard);
        services.AddSingleton(demand);

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
        ITraceCollector traceCollector = tracer ?? (ITraceCollector)new NoOpTraceCollector();
        services.AddSingleton(traceCollector);
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

        var capturingHub = new CapturingHub();
        services.AddSingleton<IHubContext<TelemetryHub>>(capturingHub);
        hub = capturingHub;

        services.AddSingleton<PlanReviewCompletionService>();

        return services.BuildServiceProvider();
    }

    private static PlanOrchestrationInput SampleInput()
    {
        ISpecialistAgent scorecard = MakeSpecialist("scorecard", "", null);
        ISpecialistAgent demand = MakeSpecialist("demand-forecasting", "", null);
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

    // step 0 = scorecard, step 1 = clarify (paused), step 2 = demand-forecasting.
    private static string PlannerJsonClarifyAtStepOne() => /*lang=json,strict*/ @"{ ""steps"": [
        { ""specialist_key"": ""scorecard"", ""intent"": ""scorecard"", ""action"": ""step-0"" },
        { ""specialist_key"": ""scorecard"", ""intent"": ""scorecard"", ""action"": ""[[CLARIFY]] Which region?"" },
        { ""specialist_key"": ""demand-forecasting"", ""intent"": ""demand"", ""action"": ""step-2"" }
    ] }";

    // step 0 = scorecard (completes with chart + prefix result),
    // step 1 = [[REPLAN]] scorecard step (suspends the plan mid-execution),
    // step 2 = demand-forecasting (the post-[[REPLAN]] step the reviewer approves next).
    private static string PlannerJsonReplanAtStepOne() => /*lang=json,strict*/ @"{ ""steps"": [
        { ""specialist_key"": ""scorecard"", ""intent"": ""scorecard"", ""action"": ""prefix-step"" },
        { ""specialist_key"": ""scorecard"", ""intent"": ""scorecard"", ""action"": ""[[REPLAN]] scope too broad"" },
        { ""specialist_key"": ""demand-forecasting"", ""intent"": ""demand"", ""action"": ""post-replan-step"" }
    ] }";

    private static ISpecialistAgent MakeSpecialist(
        string key, string reply, IReadOnlyList<ChartSpec>? charts)
    {
        var m = new Mock<ISpecialistAgent>();
        m.SetupGet(a => a.Key).Returns(key);
        m.SetupGet(a => a.DisplayName).Returns(key);
        m.SetupGet(a => a.Model).Returns("gpt-test");
        m.SetupGet(a => a.SupportedIntents).Returns([key]);
        m.Setup(a => a.HandleAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .Returns((ChatRequest req, CancellationToken _) =>
            {
                List<ChartSpec>? chartList = charts is null ? null : [.. charts];
                return Task.FromResult(new ChatResponse(
                    string.IsNullOrEmpty(reply) ? $"{key}-reply" : reply,
                    req.SessionId ?? "s", [], chartList, 10, new TokenUsage(1, 1, 2)));
            });
        return m.Object;
    }

    // ── Support doubles ──────────────────────────────────────────────────

    /// <summary>
    /// Plan store fixture for finding 3. Behaves as
    /// <see cref="PlanReviewCompletionServiceTests.InMemoryPlanStore"/> until
    /// <see cref="ExplodeOnTerminalTransitions"/> is flipped, after which any
    /// non-<see cref="PlanStatus.Running"/> / non-Awaiting plan-status update
    /// throws. Models a transient store fault between the
    /// "AwaitingReview → Running" claim and the terminal write that the
    /// executor's finally block issues.
    /// </summary>
    private sealed class ExplodingPlanStore : IPlanStore
    {
        private readonly PlanReviewCompletionServiceTests.InMemoryPlanStore _inner = new();
        public bool ExplodeOnTerminalTransitions { get; set; }

        public Task CreatePlanAsync(PlanWrite plan, CancellationToken ct = default)
            => _inner.CreatePlanAsync(plan, ct);

        public Task UpdatePlanStatusAsync(PlanStatusUpdate update, CancellationToken ct = default)
        {
            // Simulate the specific fault that surfaces finding 3: the
            // executor's finally block writes the derived terminal status
            // (Completed on the happy path) and the store rejects it, so the
            // executor throws out of ExecuteAsync while the plan record is
            // still Running. The completion service's own recovery write
            // (Failed) must be allowed through — that's the whole point of
            // the fix: contain the fault and finalize the row anyway.
            return ExplodeOnTerminalTransitions
                && string.Equals(update.Status, PlanStatus.Completed, StringComparison.Ordinal)
                ? throw new InvalidOperationException(
                    $"simulated transient failure writing plan status '{update.Status}' for {update.PlanId}")
                : _inner.UpdatePlanStatusAsync(update, ct);
        }

        public Task UpdateStepAsync(PlanStepUpdate update, CancellationToken ct = default)
            => _inner.UpdateStepAsync(update, ct);

        public Task<IReadOnlyList<PlanSummaryDto>> ListPlansForSubjectAsync(string subject, CancellationToken ct = default)
            => _inner.ListPlansForSubjectAsync(subject, ct);

        public Task<PlanDetailDto?> GetPlanAsync(string subject, string planId, CancellationToken ct = default)
            => _inner.GetPlanAsync(subject, planId, ct);

        public Task<bool> DeletePlanAsync(string subject, string planId, CancellationToken ct = default)
            => _inner.DeletePlanAsync(subject, planId, ct);

        public Task<PlanCleanupResult> PurgeExpiredAsync(DateTimeOffset olderThan, CancellationToken ct = default)
            => _inner.PurgeExpiredAsync(olderThan, ct);

        public string? LastStatusFor(string planId, string subject)
            => _inner.GetLastStatusUpdate(subject, planId)?.Status;
    }

    private sealed class RecordingTraceCollector : ITraceCollector
    {
        public ConcurrentQueue<TraceSpan> Spans { get; } = new();
        public void CaptureSpan(TraceSpan span) => Spans.Enqueue(span);
        public IReadOnlyList<TraceSpan>? GetSpans(string traceId) => null;
        public TraceSummary? GetSummary(string traceId) => null;
        public IReadOnlyList<TraceSummary> GetRecentTraces(int count = 20) => [];
        public StructuredTraceSummary? GetStructuredSummary(string traceId) => null;
        public IReadOnlyList<ToolUsageStat> GetToolStats(DateTimeOffset since, int top = 10) => [];
        public int TraceCount => Spans.Count;
        public int Capacity => 100;
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

    /// <summary>
    /// Captures the last <c>plan_final_response</c> payload so tests can
    /// assert the SignalR broadcast shape without a real hub connection.
    /// </summary>
    internal sealed class CapturingHub : IHubContext<TelemetryHub>
    {
        public object? LastFinalPayload { get; private set; }

        public IHubClients Clients { get; }
        public IGroupManager Groups { get; } = new StubGroupManager();

        public CapturingHub()
        {
            Clients = new CapturingClients(this);
        }

        private sealed class CapturingClients(CapturingHub owner) : IHubClients
        {
            public IClientProxy All { get; } = new CapturingProxy(owner);
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new CapturingProxy(owner);
            public IClientProxy Client(string connectionId) => new CapturingProxy(owner);
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new CapturingProxy(owner);
            public IClientProxy Group(string groupName) => new CapturingProxy(owner);
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new CapturingProxy(owner);
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => new CapturingProxy(owner);
            public IClientProxy User(string userId) => new CapturingProxy(owner);
            public IClientProxy Users(IReadOnlyList<string> userIds) => new CapturingProxy(owner);
        }

        private sealed class CapturingProxy(CapturingHub owner) : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            {
                if (string.Equals(method, "plan_final_response", StringComparison.Ordinal) && args.Length > 0)
                {
                    owner.LastFinalPayload = args[0];
                }
                return Task.CompletedTask;
            }
        }

        private sealed class StubGroupManager : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}

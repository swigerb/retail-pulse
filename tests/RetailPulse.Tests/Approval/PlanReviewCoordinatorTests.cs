using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using RetailPulse.Api.Approval;
using RetailPulse.Contracts.Approval;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Tests.Approval;

/// <summary>
/// Coordinator-level tests for the plan review gate (#94). Uses a real
/// <see cref="SqliteApprovalGate"/> against a temp SQLite file so the
/// persistence surface (Kind/PlanId/RoundNumber/Payload/ResponsePayload) is
/// exercised end-to-end. Every timeout scenario uses an injected
/// <see cref="TimeProvider"/> so the wall clock is never touched.
/// </summary>
public sealed class PlanReviewCoordinatorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _checkpointDir;

    public PlanReviewCoordinatorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"plan_review_{Guid.NewGuid():N}.db");
        _checkpointDir = Path.Combine(Path.GetTempPath(), $"plan_review_ckpt_{Guid.NewGuid():N}");
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

    private SqliteApprovalGate CreateGate(TimeProvider clock, TimeSpan? defaultTimeout = null) =>
        new(_dbPath, Mock.Of<ILogger<SqliteApprovalGate>>(),
            defaultTimeout ?? TimeSpan.FromMinutes(30), clock);

    /// <summary>
    /// Real-clock gate for the non-timeout tests. The FakeClock's timer plumbing
    /// works when explicitly Advanced (see the timeout test), but the gate's
    /// exponential-backoff poll uses Task.Delay(..., TimeProvider) which never
    /// resumes on its own without an Advance — so tests that only care about
    /// the decision path use System time and rely on the SQLite gate's fast
    /// initial backoff (~250 ms) to observe the human response.
    /// </summary>
    private SqliteApprovalGate CreateSystemGate(TimeSpan? defaultTimeout = null) =>
        new(_dbPath, Mock.Of<ILogger<SqliteApprovalGate>>(),
            defaultTimeout ?? TimeSpan.FromSeconds(30), TimeProvider.System);

    private PlanReviewCheckpointService CreateCheckpointService()
    {
        FileSystemJsonCheckpointStore store =
            new(new DirectoryInfo(_checkpointDir));
        var manager = CheckpointManager.CreateJson(store, customOptions: null);
        return new PlanReviewCheckpointService(store, manager, Mock.Of<ILogger<PlanReviewCheckpointService>>());
    }

    private PlanReviewCoordinator CreateCoordinator(
        SqliteApprovalGate gate,
        PlanReviewOptions options,
        TimeProvider clock,
        IPlanReviewReplanner? replanner = null) =>
        new(
            gate,
            Options.Create(options),
            CreateCheckpointService(),
            Mock.Of<ILogger<PlanReviewCoordinator>>(),
            replanner: replanner,
            timeProvider: clock);

    private static IReadOnlyList<PlanReviewStepDto> SampleSteps() =>
    [
        new() { SpecialistKey = "scorecard", Intent = "scorecard", Action = "Portfolio summary." },
        new() { SpecialistKey = "demand-forecasting", Intent = "demand", Action = "Forecast Q4." },
    ];

    private static PlanReviewCoordinationInput SampleInput() => new()
    {
        PlanId = Guid.NewGuid().ToString("N"),
        Subject = "user-1",
        Request = "Compare Q4 lift vs plan.",
        InitialSteps = SampleSteps(),
        SpecialistKeys = ["scorecard", "demand-forecasting"],
        DetectedIntents = ["scorecard", "demand-forecasting"],
    };

    // ── Approve path ─────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_returns_original_steps_and_persists_approve_row()
    {
        SqliteApprovalGate gate = CreateSystemGate();
        var options = new PlanReviewOptions { DefaultReviewTimeout = TimeSpan.FromSeconds(30), MaxReplanRounds = 0 };
        PlanReviewCoordinator coord = CreateCoordinator(gate, options, TimeProvider.System);

        PlanReviewCoordinationInput input = SampleInput();

        // Human decision arrives on a background waiter as soon as the row exists.
        Task<PlanReviewOutcome> outcomeTask = coord.CoordinateAsync(input, CancellationToken.None);

        ApprovalRequest row = await WaitForPending(gate, input.Subject);
        row.Context.Kind.Should().Be(ApprovalKind.PlanReview);
        row.Context.PlanId.Should().Be(input.PlanId);
        row.Context.RoundNumber.Should().Be(0);
        row.Context.Payload.Should().NotBeNullOrWhiteSpace();

        var payload = new PlanReviewResponsePayload { Kind = PlanReviewKinds.Approve };
        await gate.RespondAsync(
            row.RequestId,
            ApprovalDecision.Approved,
            comment: "LGTM",
            responsePayload: JsonSerializer.Serialize(payload, _json));

        PlanReviewOutcome outcome = await outcomeTask;

        outcome.IsApproved.Should().BeTrue();
        outcome.FinalSteps.Should().BeEquivalentTo(input.InitialSteps);
        outcome.TerminalReason.Should().Be(PlanReviewTerminalReason.ReviewerApproved);
        outcome.FinalRound.Should().Be(0);

        ApprovalRequest stored = (await gate.GetHistoryAsync(50)).Single();
        stored.Decision.Should().Be(ApprovalDecision.Approved);
        stored.Context.Kind.Should().Be(ApprovalKind.PlanReview);
    }

    // ── Edit path — edited plan is what actually executes ────────────────

    [Fact]
    public async Task Edit_returns_edited_steps_and_persists_modified_row()
    {
        SqliteApprovalGate gate = CreateSystemGate();
        var options = new PlanReviewOptions();
        PlanReviewCoordinator coord = CreateCoordinator(gate, options, TimeProvider.System);

        PlanReviewCoordinationInput input = SampleInput();
        Task<PlanReviewOutcome> outcomeTask = coord.CoordinateAsync(input, CancellationToken.None);
        ApprovalRequest row = await WaitForPending(gate, input.Subject);

        var edited = new List<PlanReviewStepDto>
        {
            new() { SpecialistKey = "scorecard", Intent = "scorecard", Action = "Portfolio summary (limit to top 3)." },
        };
        var payload = new PlanReviewResponsePayload
        {
            Kind = PlanReviewKinds.Edit,
            EditedSteps = edited,
        };
        await gate.RespondAsync(
            row.RequestId, ApprovalDecision.Modified, "trimmed",
            responsePayload: JsonSerializer.Serialize(payload, _json));

        PlanReviewOutcome outcome = await outcomeTask;
        outcome.IsApproved.Should().BeTrue();
        outcome.FinalSteps.Should().HaveCount(1);
        outcome.FinalSteps[0].Action.Should().Contain("limit to top 3");
        outcome.TerminalReason.Should().Be(PlanReviewTerminalReason.ReviewerEdited);
    }

    // ── Edit validation ─────────────────────────────────────────────────

    [Fact]
    public async Task Edit_with_unknown_specialist_terminates_with_EditInvalid()
    {
        SqliteApprovalGate gate = CreateSystemGate();
        var options = new PlanReviewOptions();
        PlanReviewCoordinator coord = CreateCoordinator(gate, options, TimeProvider.System);

        PlanReviewCoordinationInput input = SampleInput();
        Task<PlanReviewOutcome> outcomeTask = coord.CoordinateAsync(input, CancellationToken.None);
        ApprovalRequest row = await WaitForPending(gate, input.Subject);

        var edited = new List<PlanReviewStepDto>
        {
            new() { SpecialistKey = "not-a-real-agent", Intent = "x", Action = "x" },
        };
        var payload = new PlanReviewResponsePayload { Kind = PlanReviewKinds.Edit, EditedSteps = edited };
        await gate.RespondAsync(
            row.RequestId, ApprovalDecision.Modified, "typo",
            responsePayload: JsonSerializer.Serialize(payload, _json));

        PlanReviewOutcome outcome = await outcomeTask;
        outcome.IsApproved.Should().BeFalse();
        outcome.TerminalReason.Should().Be(PlanReviewTerminalReason.EditInvalid);
    }

    [Fact]
    public async Task Edit_to_empty_terminates_with_EditedToEmpty()
    {
        SqliteApprovalGate gate = CreateSystemGate();
        var options = new PlanReviewOptions();
        PlanReviewCoordinator coord = CreateCoordinator(gate, options, TimeProvider.System);

        PlanReviewCoordinationInput input = SampleInput();
        Task<PlanReviewOutcome> outcomeTask = coord.CoordinateAsync(input, CancellationToken.None);
        ApprovalRequest row = await WaitForPending(gate, input.Subject);

        var payload = new PlanReviewResponsePayload
        {
            Kind = PlanReviewKinds.Edit,
            EditedSteps = [],
        };
        await gate.RespondAsync(
            row.RequestId, ApprovalDecision.Modified, "drop",
            responsePayload: JsonSerializer.Serialize(payload, _json));

        PlanReviewOutcome outcome = await outcomeTask;
        outcome.TerminalReason.Should().Be(PlanReviewTerminalReason.EditedToEmpty);
    }

    // ── Replan bound ─────────────────────────────────────────────────────

    [Fact]
    public async Task Replan_bound_terminates_with_ReplanExhausted_when_cap_hit()
    {
        // MaxReplanRounds = 1 → allow round 0 + one replan (round 1). Rejecting
        // round 1 exhausts the cap and returns ReplanExhausted.
        SqliteApprovalGate gate = CreateSystemGate();
        var options = new PlanReviewOptions { MaxReplanRounds = 1 };

        var replanner = new StubReplanner(SampleSteps());
        PlanReviewCoordinator coord = CreateCoordinator(gate, options, TimeProvider.System, replanner);

        PlanReviewCoordinationInput input = SampleInput() with { Roster = FakeRoster() };

        Task<PlanReviewOutcome> outcomeTask = coord.CoordinateAsync(input, CancellationToken.None);

        // Round 0: reject with feedback → coordinator replans → round 1.
        ApprovalRequest r0 = await WaitForPendingRound(gate, input.Subject, expectedRound: 0);
        await Reject(gate, r0.RequestId, "narrower scope please");

        // Round 1: reject again → cap exhausted.
        ApprovalRequest r1 = await WaitForPendingRound(gate, input.Subject, expectedRound: 1);
        await Reject(gate, r1.RequestId, "still too broad");

        PlanReviewOutcome outcome = await outcomeTask;
        outcome.IsApproved.Should().BeFalse();
        outcome.TerminalReason.Should().Be(PlanReviewTerminalReason.ReplanExhausted);
        outcome.Rounds.Should().HaveCount(2, "one row per round including the final rejected one");

        replanner.Calls.Should().Be(1, "exactly one replan invocation between rounds 0 and 1.");

        // Every row is a plan_review row.
        (await gate.GetHistoryAsync(50)).All(r => r.Context.Kind == ApprovalKind.PlanReview).Should().BeTrue();
    }

    [Fact]
    public async Task Reject_then_approve_returns_revised_steps()
    {
        SqliteApprovalGate gate = CreateSystemGate();
        var options = new PlanReviewOptions { MaxReplanRounds = 2 };

        var revisedSteps = new List<PlanReviewStepDto>
        {
            new() { SpecialistKey = "scorecard", Intent = "scorecard", Action = "Narrower portfolio summary." },
        };
        var replanner = new StubReplanner(revisedSteps);
        PlanReviewCoordinator coord = CreateCoordinator(gate, options, TimeProvider.System, replanner);

        PlanReviewCoordinationInput input = SampleInput() with { Roster = FakeRoster() };
        Task<PlanReviewOutcome> outcomeTask = coord.CoordinateAsync(input, CancellationToken.None);

        ApprovalRequest r0 = await WaitForPendingRound(gate, input.Subject, expectedRound: 0);
        await Reject(gate, r0.RequestId, "narrow it down");

        ApprovalRequest r1 = await WaitForPendingRound(gate, input.Subject, expectedRound: 1);
        await Approve(gate, r1.RequestId);

        PlanReviewOutcome outcome = await outcomeTask;
        outcome.IsApproved.Should().BeTrue();
        outcome.FinalRound.Should().Be(1, "revised plan approved in round 1.");
        outcome.FinalSteps.Should().BeEquivalentTo(revisedSteps);
    }

    // ── Timeout with injected clock ─────────────────────────────────────

    [Fact]
    public async Task Timeout_advances_deterministically_via_injected_clock()
    {
        var clock = new ApprovalLifecycleTests.FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        SqliteApprovalGate gate = CreateGate(clock, defaultTimeout: TimeSpan.FromMinutes(30));
        var options = new PlanReviewOptions { DefaultReviewTimeout = TimeSpan.FromMinutes(5) };
        PlanReviewCoordinator coord = CreateCoordinator(gate, options, clock);

        PlanReviewCoordinationInput input = SampleInput();
        Task<PlanReviewOutcome> outcomeTask = coord.CoordinateAsync(input, CancellationToken.None);
        _ = await WaitForPending(gate, input.Subject);

        // Advance past the review timeout — the coordinator's WaitForApprovalAsync
        // must see the deadline cross and transition the row to TimedOut.
        for (int i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await Task.Yield();
        }

        PlanReviewOutcome outcome = await outcomeTask;
        outcome.IsApproved.Should().BeFalse();
        outcome.TerminalReason.Should().Be(PlanReviewTerminalReason.ReviewTimedOut);
    }

    // ── Restart during review ────────────────────────────────────────────

    [Fact]
    public async Task Restart_during_review_row_survives_and_resume_strategy_adopts_it()
    {
        // First "process" creates the row and hangs.
        string requestId;
        {
            SqliteApprovalGate firstGate = CreateSystemGate();
            var options = new PlanReviewOptions { DefaultReviewTimeout = TimeSpan.FromMinutes(30) };
            PlanReviewCoordinator coord = CreateCoordinator(firstGate, options, TimeProvider.System);

            PlanReviewCoordinationInput input = SampleInput();
            Task<PlanReviewOutcome> coordTask = coord.CoordinateAsync(input, CancellationToken.None);
            ApprovalRequest row = await WaitForPending(firstGate, input.Subject);
            requestId = row.RequestId;

            // Simulate crash: abandon the coordinator waiter.
            _ = coordTask; // no observation
        }

        // New "process": a fresh gate + reconciliation with the plan-aware strategy
        // must ADOPT (Resume) the plan_review row, not orphan it.
        SqliteApprovalGate secondGate = CreateSystemGate();
        int terminated = await secondGate.ReconcilePendingAsync(new PlanReviewResumeStrategy());

        terminated.Should().Be(0, "plan_review rows adopt across restart; they never orphan.");
        ApprovalResult result = await secondGate.GetResultAsync(requestId);
        result.Decision.Should().Be(ApprovalDecision.Pending, "row is adopted, not terminated.");
    }

    [Fact]
    public async Task Restart_tool_row_still_orphans_under_PlanReviewResumeStrategy()
    {
        SqliteApprovalGate firstGate = CreateSystemGate();
        var toolCtx = new ApprovalContext(
            AgentId: "demand-forecasting",
            UserId: "user-1",
            Action: "Increase prod 20%",
            Impact: "low",
            Urgency: "medium",
            Reasoning: "seasonal shift");
        ApprovalRequest req = await firstGate.RequestApprovalAsync(toolCtx);

        SqliteApprovalGate secondGate = CreateSystemGate();
        int terminated = await secondGate.ReconcilePendingAsync(new PlanReviewResumeStrategy());

        terminated.Should().Be(1, "tool rows retain the #91 orphan-on-restart semantics.");
        ApprovalResult result = await secondGate.GetResultAsync(req.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Orphaned);
    }

    // ── Cross-subject denial (at the persistence layer) ─────────────────

    [Fact]
    public async Task Cross_subject_pending_query_does_not_expose_another_subject_row()
    {
        SqliteApprovalGate gate = CreateSystemGate();
        var options = new PlanReviewOptions();
        PlanReviewCoordinator coord = CreateCoordinator(gate, options, TimeProvider.System);

        PlanReviewCoordinationInput input = SampleInput() with { Subject = "alice" };
        _ = coord.CoordinateAsync(input, CancellationToken.None);
        _ = await WaitForPending(gate, input.Subject);

        (await gate.GetPendingAsync("bob")).Should().BeEmpty(
            "GetPendingAsync is subject-scoped at SQL — bob must never see alice's plan review.");
    }

    // ── Audit trail across paths ────────────────────────────────────────

    [Fact]
    public async Task All_decisions_written_to_shared_history_audit_trail()
    {
        SqliteApprovalGate gate = CreateSystemGate();
        var options = new PlanReviewOptions();
        PlanReviewCoordinator coord = CreateCoordinator(gate, options, TimeProvider.System);

        // Approve → history includes plan_review + Approved
        PlanReviewCoordinationInput a = SampleInput();
        Task<PlanReviewOutcome> aTask = coord.CoordinateAsync(a, CancellationToken.None);
        ApprovalRequest aRow = await WaitForPending(gate, a.Subject);
        await Approve(gate, aRow.RequestId);
        _ = await aTask;

        // Also record a plain tool approval for parity — history must show both.
        var toolCtx = new ApprovalContext("tool-agent", "user-1", "act", "impact", "low", "why");
        ApprovalRequest tRow = await gate.RequestApprovalAsync(toolCtx);
        await gate.RespondAsync(tRow.RequestId, ApprovalDecision.Approved, "OK");

        IReadOnlyList<ApprovalRequest> history = await gate.GetHistoryAsync(50);
        history.Should().HaveCount(2);
        history.Any(r => r.Context.Kind == ApprovalKind.PlanReview).Should().BeTrue();
        history.Any(r => r.Context.Kind == ApprovalKind.Tool).Should().BeTrue();
    }

    // ── ApprovalTool unchanged ───────────────────────────────────────────

    [Fact]
    public async Task ApprovalTool_flow_default_kind_and_history_shape_unchanged()
    {
        // Default construction of ApprovalContext (as used by ApprovalTool) must
        // land Kind = "tool" without any explicit setter — this proves #94 is
        // strictly additive for the pre-existing single-tool path.
        SqliteApprovalGate gate = CreateSystemGate();

        var ctx = new ApprovalContext(
            AgentId: "agent",
            UserId: "user-1",
            Action: "act",
            Impact: "impact",
            Urgency: "low",
            Reasoning: "why");
        ApprovalRequest req = await gate.RequestApprovalAsync(ctx);

        req.Context.Kind.Should().Be(ApprovalKind.Tool);
        req.Context.PlanId.Should().BeNull();
        req.Context.RoundNumber.Should().Be(0);
        req.Context.Payload.Should().BeNull();

        await gate.RespondAsync(req.RequestId, ApprovalDecision.Approved, "ok");
        ApprovalResult result = await gate.GetResultAsync(req.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Approved);
        result.ResponsePayload.Should().BeNull("tool decisions carry no plan-shape payload.");
    }

    // ── ValidateEditedSteps unit checks ─────────────────────────────────

    [Theory]
    [InlineData("", "act", PlanReviewTerminalReason.EditInvalid)]
    [InlineData("scorecard", "", PlanReviewTerminalReason.EditInvalid)]
    [InlineData("unknown", "act", PlanReviewTerminalReason.EditInvalid)]
    public void ValidateEditedSteps_rejects_bad_shapes(string key, string action, string expected)
    {
        var edited = new List<PlanReviewStepDto>
        {
            new() { SpecialistKey = key, Intent = "x", Action = action },
        };
        string? reason = PlanReviewCoordinator.ValidateEditedSteps(edited, ["scorecard"]);
        reason.Should().Be(expected);
    }

    [Fact]
    public void ValidateEditedSteps_empty_returns_EditedToEmpty()
    {
        string? reason = PlanReviewCoordinator.ValidateEditedSteps([], ["scorecard"]);
        reason.Should().Be(PlanReviewTerminalReason.EditedToEmpty);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static async Task<ApprovalRequest> WaitForPending(SqliteApprovalGate gate, string subject)
    {
        for (int i = 0; i < 400; i++)
        {
            IReadOnlyList<ApprovalRequest> pending = await gate.GetPendingAsync(subject);
            if (pending.Count > 0) return pending[^1];
            await Task.Delay(10);
        }
        throw new InvalidOperationException("Timed out waiting for pending approval row.");
    }

    private static async Task<ApprovalRequest> WaitForPendingRound(
        SqliteApprovalGate gate, string subject, int expectedRound)
    {
        for (int i = 0; i < 400; i++)
        {
            IReadOnlyList<ApprovalRequest> pending = await gate.GetPendingAsync(subject);
            ApprovalRequest? hit = pending.FirstOrDefault(r => r.Context.RoundNumber == expectedRound);
            if (hit is not null) return hit;
            await Task.Delay(10);
        }
        throw new InvalidOperationException($"Timed out waiting for round {expectedRound}.");
    }

    private static Task Approve(SqliteApprovalGate gate, string requestId)
    {
        var payload = new PlanReviewResponsePayload { Kind = PlanReviewKinds.Approve };
        return gate.RespondAsync(requestId, ApprovalDecision.Approved, "ok",
            responsePayload: JsonSerializer.Serialize(payload, _json));
    }

    private static Task Reject(SqliteApprovalGate gate, string requestId, string feedback)
    {
        var payload = new PlanReviewResponsePayload { Kind = PlanReviewKinds.Reject, Feedback = feedback };
        return gate.RespondAsync(requestId, ApprovalDecision.Rejected, feedback,
            responsePayload: JsonSerializer.Serialize(payload, _json));
    }

    private static IReadOnlyList<ISpecialistAgent> FakeRoster()
    {
        var scorecard = new Mock<ISpecialistAgent>();
        scorecard.SetupGet(a => a.Key).Returns("scorecard");
        scorecard.SetupGet(a => a.DisplayName).Returns("Scorecard");
        scorecard.SetupGet(a => a.SupportedIntents).Returns(["scorecard"]);

        var demand = new Mock<ISpecialistAgent>();
        demand.SetupGet(a => a.Key).Returns("demand-forecasting");
        demand.SetupGet(a => a.DisplayName).Returns("Demand");
        demand.SetupGet(a => a.SupportedIntents).Returns(["demand"]);

        return [scorecard.Object, demand.Object];
    }

    /// <summary>
    /// Fixed replanner used by the reject/approve-with-revised-plan and
    /// replan-cap tests. Records the number of replan invocations so the loop
    /// bound can be asserted directly.
    /// </summary>
    private sealed class StubReplanner : IPlanReviewReplanner
    {
        private readonly IReadOnlyList<PlanReviewStepDto> _steps;
        public int Calls { get; private set; }
        public StubReplanner(IReadOnlyList<PlanReviewStepDto> steps)
        {
            _steps = steps;
        }

        public Task<Api.Agents.Planning.PlanBuildResult> ReplanAsync(
            string revisedRequest,
            IReadOnlyList<ISpecialistAgent> roster,
            IReadOnlyList<string> detectedIntents,
            CancellationToken ct)
        {
            Calls++;
            var steps = _steps
                .Select(s => new Api.Agents.Planning.PlannerStep
                {
                    SpecialistKey = s.SpecialistKey,
                    Intent = s.Intent,
                    Action = s.Action,
                })
                .ToList();
            return Task.FromResult(new Api.Agents.Planning.PlanBuildResult
            {
                Steps = steps,
                InputTokens = 1,
                OutputTokens = 1,
                TotalTokens = 2,
                Model = "test",
            });
        }
    }
}

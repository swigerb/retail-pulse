using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using RetailPulse.Api.Approval;
using RetailPulse.Contracts.Approval;
using RetailPulse.Tests.TestInfrastructure;

namespace RetailPulse.Tests.Approval;

/// <summary>
/// Issue #91 — approval lifecycle hardening. Exercises the durable-restart,
/// idempotent-reconciliation, deterministic-clock-timeout, and simultaneous
/// approve-vs-timeout race invariants using an injected <see cref="TimeProvider"/>
/// so no test touches the wall clock.
/// </summary>
public sealed class ApprovalLifecycleTests : IDisposable
{
    private readonly string _dbPath;

    public ApprovalLifecycleTests()
    {
        _dbPath = SqliteTestCleanup.NewDbPath("approval_lifecycle");
    }

    public void Dispose()
    {
        SqliteTestCleanup.ReleaseAndDelete(_dbPath);
    }

    private SqliteApprovalGate CreateGate(TimeProvider clock, TimeSpan? defaultTimeout = null)
        => new(_dbPath, Mock.Of<ILogger<SqliteApprovalGate>>(), defaultTimeout ?? TimeSpan.FromMinutes(5), clock);

    /// <summary>
    /// Polls <paramref name="condition"/> on a small logical cadence until it
    /// returns true or <paramref name="timeout"/> elapses. Tests use this to wait
    /// for a background task (typically the exponential-backoff waiter inside
    /// <see cref="SqliteApprovalGate.WaitForApprovalAsync"/>) to reach
    /// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> and
    /// register a timer with the fake clock — the pre-condition for
    /// <see cref="FakeClock.Advance"/> to deterministically wake it.
    /// </summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    private static ApprovalContext MakeContext(
        string agentId = "agent-1",
        string userId = "user-1",
        string action = "Test action",
        string? sessionId = null,
        string? conversationId = null) =>
        new(agentId, userId, action, "Low", "medium", "Testing", sessionId, conversationId);

    // ────────────────────────────────────────────────────────────────────
    // Restart reconciliation — the core scenario the bug describes:
    // create a request, drop the gate (simulated crash), a fresh gate
    // reconciles the abandoned row so a later approval attempt cannot
    // silently succeed on a request whose execution is gone.
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Restart_AbandonedPendingRow_IsOrphanedByReconciliation()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

        string requestId;
        SqliteApprovalGate firstGate = CreateGate(clock);
        ApprovalRequest req = await firstGate.RequestApprovalAsync(MakeContext(sessionId: "s-1", conversationId: "c-1"));
        requestId = req.RequestId;
        // Simulate process exit: drop the reference to the gate; the row is still
        // Pending on disk owned by firstGate.InstanceId.
        string previousInstance = firstGate.InstanceId;

        SqliteApprovalGate secondGate = CreateGate(clock);
        int terminated = await secondGate.ReconcilePendingAsync(new OrphanUnresumableStrategy());

        terminated.Should().Be(1);
        ApprovalResult result = await secondGate.GetResultAsync(requestId);
        result.Decision.Should().Be(ApprovalDecision.Orphaned);
        result.TerminalReason.Should().Be(SqliteApprovalGate.ReasonOrphanedOnRestart);
        secondGate.InstanceId.Should().NotBe(previousInstance);
    }

    [Fact]
    public async Task Restart_ApprovingOrphanedRow_HasNoEffect()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        SqliteApprovalGate firstGate = CreateGate(clock);
        ApprovalRequest req = await firstGate.RequestApprovalAsync(MakeContext());

        SqliteApprovalGate secondGate = CreateGate(clock);
        await secondGate.ReconcilePendingAsync(new OrphanUnresumableStrategy());

        // A human trying to approve a row that reconciliation already closed must NOT
        // silently overwrite the terminal outcome. This is the user-visible guarantee
        // that a late "approve" cannot mislead the caller into thinking a dead
        // execution resumed.
        await secondGate.RespondAsync(req.RequestId, ApprovalDecision.Approved, "late");

        ApprovalResult result = await secondGate.GetResultAsync(req.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Orphaned);
        result.TerminalReason.Should().Be(SqliteApprovalGate.ReasonOrphanedOnRestart);
        result.Comment.Should().NotBe("late");
    }

    [Fact]
    public async Task Reconciliation_LeavesCurrentInstancePendingRowsAlone()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        SqliteApprovalGate gate = CreateGate(clock);

        ApprovalRequest req = await gate.RequestApprovalAsync(MakeContext());

        // Same gate reconciles: the row belongs to this instance, so it must stay Pending.
        int terminated = await gate.ReconcilePendingAsync(new OrphanUnresumableStrategy());

        terminated.Should().Be(0);
        ApprovalResult result = await gate.GetResultAsync(req.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Pending);
    }

    [Fact]
    public async Task Reconciliation_IsIdempotent()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        SqliteApprovalGate firstGate = CreateGate(clock);
        ApprovalRequest req = await firstGate.RequestApprovalAsync(MakeContext());

        SqliteApprovalGate secondGate = CreateGate(clock);
        var strategy = new CountingStrategy();

        int first = await secondGate.ReconcilePendingAsync(strategy);
        int second = await secondGate.ReconcilePendingAsync(strategy);
        int third = await secondGate.ReconcilePendingAsync(strategy);

        first.Should().Be(1);
        second.Should().Be(0, "second sweep has no Pending rows left to orphan");
        third.Should().Be(0);
        // The strategy is only consulted for candidate Pending rows on each pass, and
        // after the first pass there are no such rows — so it was called exactly once.
        strategy.Calls.Should().Be(1);

        ApprovalResult result = await secondGate.GetResultAsync(req.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Orphaned);
    }

    [Fact]
    public async Task Reconciliation_ResumeAction_AdoptsRowInsteadOfOrphaning()
    {
        // Wave 2 seam behaviour: a resume strategy keeps the row Pending and the
        // reconciliation transfers ownership to the current instance so the next
        // sweep leaves it alone.
        var clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        SqliteApprovalGate firstGate = CreateGate(clock);
        ApprovalRequest req = await firstGate.RequestApprovalAsync(MakeContext(sessionId: "s-42"));

        SqliteApprovalGate secondGate = CreateGate(clock);
        var resumeStrategy = new ResumeStrategy();

        int firstSweep = await secondGate.ReconcilePendingAsync(resumeStrategy);
        int secondSweep = await secondGate.ReconcilePendingAsync(resumeStrategy);

        firstSweep.Should().Be(0, "resume keeps the row Pending");
        secondSweep.Should().Be(0, "second sweep sees the row is now owned by this instance");
        resumeStrategy.Calls.Should().Be(1);

        ApprovalResult result = await secondGate.GetResultAsync(req.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Pending);

        // And a subsequent human response is now honoured because the resumed
        // execution owns the row.
        await secondGate.RespondAsync(req.RequestId, ApprovalDecision.Approved, "resumed");
        ApprovalResult after = await secondGate.GetResultAsync(req.RequestId);
        after.Decision.Should().Be(ApprovalDecision.Approved);
        after.TerminalReason.Should().Be(SqliteApprovalGate.ReasonHumanApproved);
    }

    // ────────────────────────────────────────────────────────────────────
    // Injected-clock timeout — the waiter advances only because the fake
    // clock advances; no wall-clock sleep is involved.
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InjectedClock_TimeoutFires_Deterministically()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        SqliteApprovalGate gate = CreateGate(clock, TimeSpan.FromSeconds(60));
        ApprovalRequest req = await gate.RequestApprovalAsync(MakeContext());

        Task<ApprovalResult> waitTask = gate.WaitForApprovalAsync(req.RequestId, timeout: TimeSpan.FromSeconds(60));

        // Nudge scheduled work by advancing the fake clock past the deadline in slices.
        for (int i = 0; i < 5; i++)
        {
            await Task.Yield();
            clock.Advance(TimeSpan.FromSeconds(30));
        }

        ApprovalResult result = await waitTask;
        result.Decision.Should().Be(ApprovalDecision.TimedOut);
        result.TerminalReason.Should().Be(SqliteApprovalGate.ReasonTimeout);
    }

    [Fact]
    public async Task InjectedClock_HumanResponseBeforeDeadline_WinsAndAgreesWithRow()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        SqliteApprovalGate gate = CreateGate(clock, TimeSpan.FromSeconds(60));
        ApprovalRequest req = await gate.RequestApprovalAsync(MakeContext());

        Task<ApprovalResult> waitTask = gate.WaitForApprovalAsync(req.RequestId, timeout: TimeSpan.FromSeconds(60));

        // Determinism (issue #154 sibling audit): the fake clock only wakes an
        // already-parked Task.Delay. Wait for the waiter to register its timer
        // BEFORE the human response so the sequence is fully deterministic —
        // waiter parked → human writes Approved → Advance wakes waiter → waiter
        // re-reads the row and returns the human's Approved decision.
        bool parked = await WaitUntilAsync(() => clock.TimerCount >= 1, TimeSpan.FromSeconds(5));
        parked.Should().BeTrue("the waiter must park at Task.Delay(_timeProvider) before the human responds");

        await gate.RespondAsync(req.RequestId, ApprovalDecision.Approved, "green-lit");

        // Advance clock enough to release the next backoff tick without crossing the deadline.
        clock.Advance(TimeSpan.FromSeconds(1));

        ApprovalResult result = await waitTask;
        result.Decision.Should().Be(ApprovalDecision.Approved);
        result.Comment.Should().Be("green-lit");
        result.TerminalReason.Should().Be(SqliteApprovalGate.ReasonHumanApproved);

        ApprovalResult stored = await gate.GetResultAsync(req.RequestId);
        stored.Decision.Should().Be(result.Decision);
        stored.TerminalReason.Should().Be(result.TerminalReason);
    }

    // ────────────────────────────────────────────────────────────────────
    // Simultaneous approve-vs-timeout race — the exact invariant issue
    // #91 calls out. Whichever side wins the conditional UPDATE first,
    // the waiter and the row must agree on exactly one terminal outcome.
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Race_ApproveAtTheDeadline_ExactlyOneTerminalOutcome_HumanWins()
    {
        // The waiter's timeout branch does UPDATE ... WHERE Decision = 'Pending'.
        // If the human beat the waiter to the UPDATE, the waiter's UPDATE affects 0
        // rows and it must return the human's persisted decision — not TimedOut.
        var clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        SqliteApprovalGate gate = CreateGate(clock, TimeSpan.FromSeconds(60));
        ApprovalRequest req = await gate.RequestApprovalAsync(MakeContext());

        // Simulate the human sneaking in first: the row is already Modified.
        await gate.RespondAsync(req.RequestId, ApprovalDecision.Modified, "scoped down");

        Task<ApprovalResult> waitTask = gate.WaitForApprovalAsync(req.RequestId, timeout: TimeSpan.FromSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(120));

        ApprovalResult result = await waitTask;

        result.Decision.Should().Be(ApprovalDecision.Modified, "the human already resolved the row before the waiter tried to time out");
        result.TerminalReason.Should().Be(SqliteApprovalGate.ReasonHumanModified);
        result.Comment.Should().Be("scoped down");

        ApprovalResult stored = await gate.GetResultAsync(req.RequestId);
        stored.Decision.Should().Be(result.Decision);
        stored.TerminalReason.Should().Be(result.TerminalReason);
        stored.Comment.Should().Be(result.Comment);
    }

    [Fact]
    public async Task Race_HumanApproveAndWaiterTimeout_InterleavedFireOnce_ExactlyOneWinner()
    {
        // Kick both terminal writers at the exact deadline moment repeatedly to
        // maximise the odds of an interleaving; assert the invariant on every
        // iteration: the row and the waiter always report one and only one
        // terminal decision and it is one of {Approved, TimedOut}.
        //
        // Determinism (issue #154 sibling audit): the FakeClock's Advance only
        // wakes an already-parked Task.Delay. Wait for the waiter to register
        // its timer before Advance so the deadline check is guaranteed to run.
        for (int trial = 0; trial < 15; trial++)
        {
            string dbPath = SqliteTestCleanup.NewDbPath("approval_race");
            try
            {
                var clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
                SqliteApprovalGate gate = new(dbPath, Mock.Of<ILogger<SqliteApprovalGate>>(), TimeSpan.FromSeconds(60), clock);
                ApprovalRequest req = await gate.RequestApprovalAsync(MakeContext());

                Task<ApprovalResult> waitTask = gate.WaitForApprovalAsync(req.RequestId, timeout: TimeSpan.FromSeconds(60));

                bool parked = await WaitUntilAsync(() => clock.TimerCount >= 1, TimeSpan.FromSeconds(5));
                parked.Should().BeTrue($"trial {trial}: the waiter must park at Task.Delay(_timeProvider) before Advance is called");

                var humanTask = Task.Run(async () =>
                {
                    // Give the waiter a moment to see Pending once, then race the
                    // human response against the timeout.
                    await Task.Yield();
                    await gate.RespondAsync(req.RequestId, ApprovalDecision.Approved, "raced");
                });

                clock.Advance(TimeSpan.FromSeconds(120));
                await Task.WhenAll(waitTask, humanTask);

                ApprovalResult waiter = await waitTask;
                ApprovalResult stored = await gate.GetResultAsync(req.RequestId);

                waiter.Decision.Should().BeOneOf(ApprovalDecision.Approved, ApprovalDecision.TimedOut);
                stored.Decision.Should().Be(waiter.Decision, "the returned result must match the durable row exactly");
                stored.TerminalReason.Should().Be(waiter.TerminalReason);
                stored.Comment.Should().Be(waiter.Comment);
                stored.RespondedAt.Should().Be(waiter.RespondedAt);
            }
            finally
            {
                SqliteTestCleanup.ReleaseAndDelete(dbPath);
            }
        }
    }

    [Fact]
    public async Task Race_TwoWaitersOnSameRequest_SameTerminalOutcome()
    {
        // Two waiters (e.g., a retry) observing the same request must both return
        // the same terminal outcome — the conditional UPDATE guarantees at most one
        // writer flips Pending, and the other side re-reads the actual winner.
        //
        // Determinism (issue #154 sibling audit): wait for BOTH waiters to park at
        // Task.Delay(_timeProvider) before Advance, so each waiter is guaranteed
        // to be woken by the fake clock's advance.
        var clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        SqliteApprovalGate gate = CreateGate(clock, TimeSpan.FromSeconds(60));
        ApprovalRequest req = await gate.RequestApprovalAsync(MakeContext());

        Task<ApprovalResult> a = gate.WaitForApprovalAsync(req.RequestId, timeout: TimeSpan.FromSeconds(60));
        Task<ApprovalResult> b = gate.WaitForApprovalAsync(req.RequestId, timeout: TimeSpan.FromSeconds(60));

        bool bothParked = await WaitUntilAsync(() => clock.TimerCount >= 2, TimeSpan.FromSeconds(5));
        bothParked.Should().BeTrue("both waiters must park at Task.Delay(_timeProvider) before Advance is called");

        clock.Advance(TimeSpan.FromSeconds(120));
        ApprovalResult[] results = await Task.WhenAll(a, b);

        results[0].Decision.Should().Be(results[1].Decision);
        results[0].Decision.Should().Be(ApprovalDecision.TimedOut);
        results[0].TerminalReason.Should().Be(SqliteApprovalGate.ReasonTimeout);
    }

    // ────────────────────────────────────────────────────────────────────
    // Configuration surface
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfiguredDefaultTimeout_IsUsedByRequestAndWait()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        SqliteApprovalGate gate = CreateGate(clock, TimeSpan.FromSeconds(45));

        ApprovalRequest req = await gate.RequestApprovalAsync(MakeContext());
        // Expiration on the returned record uses the configured default.
        (req.ExpiresAt - req.CreatedAt).Should().Be(TimeSpan.FromSeconds(45));

        Task<ApprovalResult> waitTask = gate.WaitForApprovalAsync(req.RequestId);

        // Determinism (issue #154 sibling audit): the fake clock only wakes an
        // already-parked Task.Delay, so wait for the waiter to register its
        // timer before advancing past the deadline.
        bool parked = await WaitUntilAsync(() => clock.TimerCount >= 1, TimeSpan.FromSeconds(5));
        parked.Should().BeTrue("the waiter must park at Task.Delay(_timeProvider) before Advance is called");

        clock.Advance(TimeSpan.FromSeconds(90));

        ApprovalResult result = await waitTask;
        result.Decision.Should().Be(ApprovalDecision.TimedOut);
    }

    [Fact]
    public async Task Correlation_SessionAndConversationIdsRoundTrip()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        SqliteApprovalGate gate = CreateGate(clock);
        ApprovalContext ctx = MakeContext(sessionId: "sess-abc", conversationId: "conv-xyz");

        await gate.RequestApprovalAsync(ctx);

        IReadOnlyList<ApprovalRequest> pending = await gate.GetPendingAsync("user-1");
        pending.Should().ContainSingle();
        pending[0].Context.SessionId.Should().Be("sess-abc");
        pending[0].Context.ConversationId.Should().Be("conv-xyz");
    }

    [Fact]
    public async Task StartupService_Skipped_WhenReconcileDisabled()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        SqliteApprovalGate firstGate = CreateGate(clock);
        ApprovalRequest req = await firstGate.RequestApprovalAsync(MakeContext());

        SqliteApprovalGate secondGate = CreateGate(clock);
        IOptions<ApprovalOptions> opts = Options.Create(new ApprovalOptions { ReconcileOnStartup = false });
        var svc = new ApprovalReconciliationBackgroundService(
            secondGate,
            new OrphanUnresumableStrategy(),
            opts,
            Mock.Of<ILogger<ApprovalReconciliationBackgroundService>>());

        await svc.StartAsync(CancellationToken.None);

        ApprovalResult result = await secondGate.GetResultAsync(req.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Pending, "reconciliation disabled leaves rows alone");
    }

    [Fact]
    public async Task StartupService_Enabled_OrphansAbandonedRow()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        SqliteApprovalGate firstGate = CreateGate(clock);
        ApprovalRequest req = await firstGate.RequestApprovalAsync(MakeContext());

        SqliteApprovalGate secondGate = CreateGate(clock);
        var svc = new ApprovalReconciliationBackgroundService(
            secondGate,
            new OrphanUnresumableStrategy(),
            Options.Create(new ApprovalOptions()),
            Mock.Of<ILogger<ApprovalReconciliationBackgroundService>>());

        await svc.StartAsync(CancellationToken.None);

        ApprovalResult result = await secondGate.GetResultAsync(req.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Orphaned);
        result.TerminalReason.Should().Be(SqliteApprovalGate.ReasonOrphanedOnRestart);
    }

    // ────────────────────────────────────────────────────────────────────
    // Endpoint / result contract — RespondAsync must return the persisted
    // winner (never a caller-echoed decision) so the HTTP response and
    // SignalR broadcast advertise exactly one user-visible outcome.
    // See #91 approval hardening.
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Respond_LateHumanResponseAfterTimeout_ReturnsPersistedTimedOutResult()
    {
        // Timeout fired via the waiter first; a subsequent human response arrives at
        // the endpoint. The endpoint must report the persisted TimedOut winner,
        // not the caller-echoed Approved decision the operator clicked.
        var clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        SqliteApprovalGate gate = CreateGate(clock, TimeSpan.FromSeconds(60));
        ApprovalRequest req = await gate.RequestApprovalAsync(MakeContext());

        Task<ApprovalResult> waitTask = gate.WaitForApprovalAsync(req.RequestId, timeout: TimeSpan.FromSeconds(60));
        for (int i = 0; i < 5; i++)
        {
            await Task.Yield();
            clock.Advance(TimeSpan.FromSeconds(30));
        }
        ApprovalResult waiter = await waitTask;
        waiter.Decision.Should().Be(ApprovalDecision.TimedOut);

        ApprovalResult endpointResult = await gate.RespondAsync(req.RequestId, ApprovalDecision.Approved, "late human");

        endpointResult.Decision.Should().Be(ApprovalDecision.TimedOut, "the persisted winner is Timeout — the endpoint MUST NOT echo the caller-requested Approved");
        endpointResult.TerminalReason.Should().Be(SqliteApprovalGate.ReasonTimeout);
        endpointResult.Comment.Should().NotBe("late human");
        endpointResult.RequestId.Should().Be(req.RequestId);

        ApprovalResult stored = await gate.GetResultAsync(req.RequestId);
        stored.Decision.Should().Be(endpointResult.Decision);
        stored.TerminalReason.Should().Be(endpointResult.TerminalReason);
        stored.Comment.Should().Be(endpointResult.Comment);
        stored.RespondedAt.Should().Be(endpointResult.RespondedAt);
    }

    [Fact]
    public async Task Respond_LateHumanResponseAfterOrphan_ReturnsPersistedOrphanedResult()
    {
        // Startup reconciliation orphaned the row before the human clicked Approve.
        // The endpoint must return the persisted Orphaned outcome, not silently
        // pretend the click succeeded.
        var clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        SqliteApprovalGate firstGate = CreateGate(clock);
        ApprovalRequest req = await firstGate.RequestApprovalAsync(MakeContext(sessionId: "s-orphan"));

        SqliteApprovalGate secondGate = CreateGate(clock);
        int terminated = await secondGate.ReconcilePendingAsync(new OrphanUnresumableStrategy());
        terminated.Should().Be(1);

        ApprovalResult endpointResult = await secondGate.RespondAsync(req.RequestId, ApprovalDecision.Approved, "late click");

        endpointResult.Decision.Should().Be(ApprovalDecision.Orphaned, "reconciliation already closed the row — the endpoint MUST NOT echo Approved");
        endpointResult.TerminalReason.Should().Be(SqliteApprovalGate.ReasonOrphanedOnRestart);
        endpointResult.Comment.Should().NotBe("late click");

        ApprovalResult stored = await secondGate.GetResultAsync(req.RequestId);
        stored.Decision.Should().Be(endpointResult.Decision);
        stored.TerminalReason.Should().Be(endpointResult.TerminalReason);
        stored.Comment.Should().Be(endpointResult.Comment);
    }

    [Fact]
    public async Task Respond_SecondHumanResponse_ReturnsFirstPersistedWinner()
    {
        // Two humans (or the same operator double-clicking) racing on one request —
        // the second RespondAsync must return the FIRST persisted decision, not a
        // synthetic echo of its own requested decision.
        var clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        SqliteApprovalGate gate = CreateGate(clock);
        ApprovalRequest req = await gate.RequestApprovalAsync(MakeContext());

        ApprovalResult first = await gate.RespondAsync(req.RequestId, ApprovalDecision.Approved, "first click");
        ApprovalResult second = await gate.RespondAsync(req.RequestId, ApprovalDecision.Rejected, "second click");

        first.Decision.Should().Be(ApprovalDecision.Approved);
        first.Comment.Should().Be("first click");
        first.TerminalReason.Should().Be(SqliteApprovalGate.ReasonHumanApproved);

        second.Decision.Should().Be(ApprovalDecision.Approved, "the second response MUST return the first persisted winner, not its own requested Rejected");
        second.Comment.Should().Be("first click");
        second.TerminalReason.Should().Be(SqliteApprovalGate.ReasonHumanApproved);
        second.RespondedAt.Should().Be(first.RespondedAt);
    }

    [Fact]
    public async Task Respond_MissingRequestId_ThrowsSoEndpointReturnsNotFound()
    {
        // The endpoint catches KeyNotFoundException and returns 404; RespondAsync
        // must surface that condition instead of silently swallowing it.
        var clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        SqliteApprovalGate gate = CreateGate(clock);

        Func<Task<ApprovalResult>> act = () => gate.RespondAsync("does-not-exist", ApprovalDecision.Approved, "ghost");
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Race_ConcurrentEndpointRespondVsWaiterTimeout_EndpointAndWaiterAgreeExactlyOnce()
    {
        // Concurrent human-vs-timeout at the endpoint boundary. RespondAsync (the
        // endpoint call) and WaitForApprovalAsync (the agent's blocking waiter) must
        // return the same terminal decision, matching the durable row exactly.
        //
        // Determinism protocol (issues #154 + #156):
        //   1. The FakeClock only wakes an already-parked Task.Delay. If we
        //      Advance before the waiter has reached
        //      Task.Delay(backoff, _timeProvider, ct) and registered its timer,
        //      Advance fires on an empty timer list and the waiter is stranded.
        //      Wait for TimerCount >= 1 so Advance is guaranteed to wake it.
        //   2. Once both sides are racing for the single conditional UPDATE, we
        //      await the tasks WITHOUT a wall-clock guard. The prior
        //      WaitAsync(TimeSpan.FromSeconds(5)) tripped a real TimeoutException
        //      under CPU contention from the ~3,396-test full suite (~1 in 9 runs
        //      even after the timer-registration fix): the FakeClock was doing
        //      its job, but SQLite I/O + thread-pool scheduling for the two
        //      terminal writes occasionally exceeded 5 s of real time. A wider
        //      guard hides the load-sensitivity rather than removing it; a
        //      logical-time guard is impossible here because the real work isn't
        //      driven by TimeProvider. If the invariant is ever broken so the
        //      waiter genuinely hangs, this test hangs — a real, visible bug.
        for (int trial = 0; trial < 15; trial++)
        {
            string dbPath = SqliteTestCleanup.NewDbPath("approval_endpoint_race");
            try
            {
                var clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
                SqliteApprovalGate gate = new(dbPath, Mock.Of<ILogger<SqliteApprovalGate>>(), TimeSpan.FromSeconds(60), clock);
                ApprovalRequest req = await gate.RequestApprovalAsync(MakeContext());

                Task<ApprovalResult> waitTask = gate.WaitForApprovalAsync(req.RequestId, timeout: TimeSpan.FromSeconds(60));

                // The waiter's first loop iteration reads Pending, heartbeats,
                // checks the deadline (not yet crossed), then awaits Task.Delay
                // against the injected clock. Only after that Task.Delay lands
                // does TimerCount reach 1.
                bool parked = await WaitUntilAsync(() => clock.TimerCount >= 1, TimeSpan.FromSeconds(5));
                parked.Should().BeTrue($"trial {trial}: the waiter must park at Task.Delay(_timeProvider) before Advance is called");

                Task<ApprovalResult> endpointTask = Task.Run(async () =>
                {
                    await Task.Yield();
                    return await gate.RespondAsync(req.RequestId, ApprovalDecision.Approved, "raced-endpoint");
                });

                clock.Advance(TimeSpan.FromSeconds(120));
                await Task.WhenAll(waitTask, endpointTask);

                ApprovalResult waiter = await waitTask;
                ApprovalResult endpoint = await endpointTask;
                ApprovalResult stored = await gate.GetResultAsync(req.RequestId);

                waiter.Decision.Should().BeOneOf(ApprovalDecision.Approved, ApprovalDecision.TimedOut);
                endpoint.Decision.Should().Be(waiter.Decision, "the endpoint MUST report the same terminal outcome the waiter observes");
                endpoint.TerminalReason.Should().Be(waiter.TerminalReason);
                endpoint.Comment.Should().Be(waiter.Comment);
                endpoint.RespondedAt.Should().Be(waiter.RespondedAt);

                stored.Decision.Should().Be(endpoint.Decision, "the endpoint result MUST match the durable row exactly");
                stored.TerminalReason.Should().Be(endpoint.TerminalReason);
                stored.Comment.Should().Be(endpoint.Comment);
                stored.RespondedAt.Should().Be(endpoint.RespondedAt);
            }
            finally
            {
                SqliteTestCleanup.ReleaseAndDelete(dbPath);
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // Test fixtures — resume strategies + fake clock. The clock also supports
    // Task.Delay(TimeSpan, TimeProvider, CancellationToken) by scheduling every
    // callback registered through CreateTimer and firing them when Advance
    // crosses their due time.
    // ────────────────────────────────────────────────────────────────────

    private sealed class CountingStrategy : IApprovalResumeStrategy
    {
        public int Calls { get; private set; }
        public Task<ApprovalResumeAction> DecideAsync(ApprovalRequest orphaned, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(ApprovalResumeAction.OrphanTerminal);
        }
    }

    private sealed class ResumeStrategy : IApprovalResumeStrategy
    {
        public int Calls { get; private set; }
        public Task<ApprovalResumeAction> DecideAsync(ApprovalRequest orphaned, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(ApprovalResumeAction.Resume);
        }
    }

    /// <summary>
    /// Deterministic <see cref="TimeProvider"/> used across the lifecycle suite.
    /// Supports <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
    /// by firing every scheduled timer when <see cref="Advance"/> covers its due time,
    /// so the exponential-backoff loop in <see cref="SqliteApprovalGate.WaitForApprovalAsync"/>
    /// runs synchronously with the test's clock advances — never with the wall clock.
    /// </summary>
    internal sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        private readonly Lock _lock = new();
        private readonly List<FakeTimer> _timers = [];

        public FakeClock(DateTimeOffset start) { _now = start; }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock) return _now;
        }

        /// <summary>
        /// Count of currently-registered (undisposed) timers. Tests await
        /// <see cref="TimerCount"/> reaching an expected value before calling
        /// <see cref="Advance"/> so the advance is guaranteed to wake at least
        /// one parked <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
        /// — otherwise Advance can fire on an empty timer list and the waiter
        /// registers its timer after time has already moved, leaving it
        /// stranded until the outer WaitAsync wall-clock timeout trips.
        /// </summary>
        public int TimerCount
        {
            get { lock (_lock) return _timers.Count; }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var t = new FakeTimer(this, callback, state, dueTime, period);
            lock (_lock) _timers.Add(t);
            return t;
        }

        public void Advance(TimeSpan delta)
        {
            List<FakeTimer> snapshot;
            lock (_lock)
            {
                _now += delta;
                snapshot = [.. _timers];
            }
            foreach (FakeTimer t in snapshot) t.Tick(delta);
        }

        internal void Remove(FakeTimer t)
        {
            lock (_lock) _timers.Remove(t);
        }
    }

    internal sealed class FakeTimer : ITimer
    {
        private readonly FakeClock _clock;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _dueTime;
        private TimeSpan _period;
        private TimeSpan _accum;
        private bool _disposed;

        public FakeTimer(FakeClock clock, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _clock = clock;
            _callback = callback;
            _state = state;
            _dueTime = dueTime;
            _period = period;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _dueTime = dueTime;
            _period = period;
            _accum = TimeSpan.Zero;
            return true;
        }

        public void Tick(TimeSpan delta)
        {
            if (_disposed) return;
            _accum += delta;
            while (!_disposed && _accum >= _dueTime && _dueTime > TimeSpan.Zero)
            {
                _accum -= _dueTime;
                _dueTime = _period > TimeSpan.Zero ? _period : Timeout.InfiniteTimeSpan;
                _callback(_state);
            }
        }

        public void Dispose() { _disposed = true; _clock.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

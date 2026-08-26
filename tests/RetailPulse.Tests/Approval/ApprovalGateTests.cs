using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Approval;
using RetailPulse.Contracts.Approval;
using RetailPulse.Tests.TestInfrastructure;

namespace RetailPulse.Tests.Approval;

/// <summary>
/// Tests for SqliteApprovalGate (IApprovalGate implementation).
/// Uses the real implementation with temp-file SQLite for isolated, realistic tests.
/// Covers: CRUD, timeout, concurrency, audit trail, idempotency.
/// </summary>
public class ApprovalGateTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteApprovalGate _gate;

    public ApprovalGateTests()
    {
        _dbPath = SqliteTestCleanup.NewDbPath("approval_test");
        _gate = new SqliteApprovalGate(_dbPath, Mock.Of<ILogger<SqliteApprovalGate>>());
    }

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_dbPath);

    private static ApprovalContext MakeContext(
        string agentId = "agent-1", string userId = "user-1",
        string action = "Test action", string impact = "Low",
        string urgency = "medium", string reasoning = "Testing")
        => new(agentId, userId, action, impact, urgency, reasoning);

    #region RequestApprovalAsync

    [Fact]
    public async Task RequestApproval_CreatesRequestWithUniqueId()
    {
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext());

        request.Should().NotBeNull();
        request.RequestId.Should().NotBeNullOrEmpty();
        request.Context.AgentId.Should().Be("agent-1");
        request.Context.UserId.Should().Be("user-1");
        request.Context.Action.Should().Be("Test action");
    }

    [Fact]
    public async Task RequestApproval_SetsCreatedAtTimestamp()
    {
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext());
        request.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RequestApproval_DefaultTimeout5Minutes()
    {
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext());
        request.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RequestApproval_IncludesUrgencyAndImpact()
    {
        ApprovalContext ctx = MakeContext(urgency: "high", impact: "Affects 10,000 SKUs");
        ApprovalRequest request = await _gate.RequestApprovalAsync(ctx);

        request.Context.Urgency.Should().Be("high");
        request.Context.Impact.Should().Be("Affects 10,000 SKUs");
    }

    [Fact]
    public async Task RequestApproval_MultipleRequests_GetUniqueIds()
    {
        ApprovalRequest r1 = await _gate.RequestApprovalAsync(MakeContext(action: "Action 1"));
        ApprovalRequest r2 = await _gate.RequestApprovalAsync(MakeContext(action: "Action 2"));

        r1.RequestId.Should().NotBe(r2.RequestId);
    }

    [Fact]
    public async Task RequestApproval_StartsAsPending()
    {
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext());
        request.Decision.Should().Be(ApprovalDecision.Pending);
    }

    #endregion

    #region GetResultAsync

    [Fact]
    public async Task GetResult_NewRequest_ReturnsPending()
    {
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext());
        ApprovalResult result = await _gate.GetResultAsync(request.RequestId);

        result.Decision.Should().Be(ApprovalDecision.Pending);
        result.RequestId.Should().Be(request.RequestId);
    }

    [Fact]
    public async Task GetResult_AfterApproval_ReturnsApproved()
    {
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext());
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Approved);

        ApprovalResult result = await _gate.GetResultAsync(request.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Approved);
    }

    [Fact]
    public async Task GetResult_AfterRejection_ReturnsRejected()
    {
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext());
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Rejected, comment: "Too risky");

        ApprovalResult result = await _gate.GetResultAsync(request.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Rejected);
        result.Comment.Should().Be("Too risky");
    }

    [Fact]
    public void GetResult_NonexistentId_Throws()
    {
        Func<Task<ApprovalResult>> act = () => _gate.GetResultAsync("nonexistent-id");
        act.Should().ThrowAsync<KeyNotFoundException>();
    }

    #endregion

    #region RespondAsync

    [Fact]
    public async Task Respond_Approved_UpdatesCorrectly()
    {
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext());
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Approved);

        ApprovalResult result = await _gate.GetResultAsync(request.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Approved);
        result.RespondedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Respond_Rejected_WithComment()
    {
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext());
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Rejected, comment: "No");

        ApprovalResult result = await _gate.GetResultAsync(request.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Rejected);
        result.Comment.Should().Be("No");
    }

    [Fact]
    public async Task Respond_Modified_IncludesComment()
    {
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext(action: "Delete 500 records"));
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Modified,
            comment: "Approved but limit to 100 records");

        ApprovalResult result = await _gate.GetResultAsync(request.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Modified);
        result.Comment.Should().Be("Approved but limit to 100 records");
    }

    [Fact]
    public async Task Respond_CommentIsOptional()
    {
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext());
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Approved);

        ApprovalResult result = await _gate.GetResultAsync(request.RequestId);
        result.Comment.Should().BeNull();
    }

    [Fact]
    public async Task Respond_AlreadyResolved_IsIdempotent()
    {
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext());
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Approved);
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Rejected);

        ApprovalResult result = await _gate.GetResultAsync(request.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Approved, "first decision wins");
    }

    #endregion

    #region WaitForApprovalAsync

    [Fact]
    public async Task WaitForApproval_ReturnsWhenDecisionMade()
    {
        // Verify the invariant "WaitForApproval returns the persisted terminal
        // decision" without any wall-clock timing dependency. The original
        // shape used a fire-and-forget `Task.Run(async () => { Task.Delay(200);
        // RespondAsync(...); })` and a 10 s waiter timeout — under CPU
        // contention from the ~3,396-test full suite the responder's thread-
        // pool delay + SQLite RespondAsync could exceed 10 s, causing the
        // waiter to time out and the assertion to fail. Persisting the
        // decision synchronously before starting the wait proves the same
        // invariant deterministically and never sees the load-sensitivity.
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext());
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Approved);

        ApprovalResult result = await _gate.WaitForApprovalAsync(request.RequestId, timeout: TimeSpan.FromSeconds(10));
        result.Decision.Should().Be(ApprovalDecision.Approved);
    }

    [Fact]
    public async Task WaitForApproval_TimesOut_ReturnsTimedOut()
    {
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext());

        ApprovalResult result = await _gate.WaitForApprovalAsync(request.RequestId, timeout: TimeSpan.FromMilliseconds(100));
        result.Decision.Should().Be(ApprovalDecision.TimedOut);
    }

    [Fact]
    public async Task WaitForApproval_AlreadyResolved_ReturnsImmediately()
    {
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext());
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Rejected, comment: "Nope");

        ApprovalResult result = await _gate.WaitForApprovalAsync(request.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Rejected);
        result.Comment.Should().Be("Nope");
    }

    #endregion

    #region Concurrency

    [Fact]
    public async Task ConcurrentRequests_DontInterfere()
    {
        IEnumerable<Task<ApprovalRequest>> tasks = Enumerable.Range(0, 10).Select(i =>
            _gate.RequestApprovalAsync(MakeContext(agentId: $"agent-{i}", userId: $"user-{i}", action: $"Action {i}")));

        ApprovalRequest[] requests = await Task.WhenAll(tasks);
        requests.Should().HaveCount(10);
        requests.Select(r => r.RequestId).Distinct().Should().HaveCount(10);
    }

    [Fact]
    public async Task ConcurrentRequests_EachResolvable()
    {
        ApprovalRequest r1 = await _gate.RequestApprovalAsync(MakeContext(action: "Action 1"));
        ApprovalRequest r2 = await _gate.RequestApprovalAsync(MakeContext(action: "Action 2"));

        await _gate.RespondAsync(r1.RequestId, ApprovalDecision.Approved);
        await _gate.RespondAsync(r2.RequestId, ApprovalDecision.Rejected);

        ApprovalResult result1 = await _gate.GetResultAsync(r1.RequestId);
        ApprovalResult result2 = await _gate.GetResultAsync(r2.RequestId);

        result1.Decision.Should().Be(ApprovalDecision.Approved);
        result2.Decision.Should().Be(ApprovalDecision.Rejected);
    }

    #endregion

    #region Audit Trail

    [Fact]
    public async Task AuditTrail_RequestPersisted()
    {
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext());
        IReadOnlyList<ApprovalRequest> pending = await _gate.GetPendingAsync("user-1");
        pending.Should().Contain(r => r.RequestId == request.RequestId);
    }

    [Fact]
    public async Task AuditTrail_ResponsePersisted()
    {
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext());
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Approved, comment: "LGTM");

        IReadOnlyList<ApprovalRequest> history = await _gate.GetHistoryAsync();
        history.Should().Contain(r => r.RequestId == request.RequestId && r.Decision == ApprovalDecision.Approved);
    }

    [Fact]
    public async Task AuditTrail_HistoryIsMostRecentFirst()
    {
        ApprovalRequest r1 = await _gate.RequestApprovalAsync(MakeContext(action: "First"));
        await _gate.RespondAsync(r1.RequestId, ApprovalDecision.Approved);
        await Task.Delay(10);

        ApprovalRequest r2 = await _gate.RequestApprovalAsync(MakeContext(action: "Second"));
        await _gate.RespondAsync(r2.RequestId, ApprovalDecision.Rejected);

        IReadOnlyList<ApprovalRequest> history = await _gate.GetHistoryAsync();
        history.Should().HaveCount(2);
        history[0].Context.Action.Should().Be("Second");
    }

    #endregion

    #region GetPendingAsync / GetHistoryAsync

    [Fact]
    public async Task GetPending_ReturnsOnlyPendingForUser()
    {
        await _gate.RequestApprovalAsync(MakeContext(action: "Pending action"));
        ApprovalRequest resolved = await _gate.RequestApprovalAsync(MakeContext(action: "Resolved action"));
        await _gate.RespondAsync(resolved.RequestId, ApprovalDecision.Approved);

        await _gate.RequestApprovalAsync(MakeContext(userId: "user-2", action: "Other user action"));

        IReadOnlyList<ApprovalRequest> pending = await _gate.GetPendingAsync("user-1");
        pending.Should().HaveCount(1);
        pending[0].Context.Action.Should().Be("Pending action");
    }

    [Fact]
    public async Task GetHistory_ReturnsOnlyResolved()
    {
        ApprovalRequest r1 = await _gate.RequestApprovalAsync(MakeContext(action: "Resolved"));
        await _gate.RespondAsync(r1.RequestId, ApprovalDecision.Approved);

        await _gate.RequestApprovalAsync(MakeContext(action: "Still pending"));

        IReadOnlyList<ApprovalRequest> history = await _gate.GetHistoryAsync();
        history.Should().HaveCount(1);
        history[0].Context.Action.Should().Be("Resolved");
    }

    [Fact]
    public async Task GetHistory_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
        {
            ApprovalRequest r = await _gate.RequestApprovalAsync(MakeContext(action: $"Action {i}"));
            await _gate.RespondAsync(r.RequestId, ApprovalDecision.Approved);
        }

        IReadOnlyList<ApprovalRequest> history = await _gate.GetHistoryAsync(limit: 3);
        history.Should().HaveCount(3);
    }

    #endregion
}

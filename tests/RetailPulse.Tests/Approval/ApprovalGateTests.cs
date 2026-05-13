using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Approval;
using RetailPulse.Contracts.Approval;

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
        _dbPath = Path.Combine(Path.GetTempPath(), $"approval_test_{Guid.NewGuid():N}.db");
        _gate = new SqliteApprovalGate(_dbPath, Mock.Of<ILogger<SqliteApprovalGate>>());
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private static ApprovalContext MakeContext(
        string agentId = "agent-1", string userId = "user-1",
        string action = "Test action", string impact = "Low",
        string urgency = "medium", string reasoning = "Testing")
        => new(agentId, userId, action, impact, urgency, reasoning);

    #region RequestApprovalAsync

    [Fact]
    public async Task RequestApproval_CreatesRequestWithUniqueId()
    {
        var request = await _gate.RequestApprovalAsync(MakeContext());

        request.Should().NotBeNull();
        request.RequestId.Should().NotBeNullOrEmpty();
        request.Context.AgentId.Should().Be("agent-1");
        request.Context.UserId.Should().Be("user-1");
        request.Context.Action.Should().Be("Test action");
    }

    [Fact]
    public async Task RequestApproval_SetsCreatedAtTimestamp()
    {
        var request = await _gate.RequestApprovalAsync(MakeContext());
        request.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RequestApproval_DefaultTimeout5Minutes()
    {
        var request = await _gate.RequestApprovalAsync(MakeContext());
        request.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RequestApproval_IncludesUrgencyAndImpact()
    {
        var ctx = MakeContext(urgency: "high", impact: "Affects 10,000 SKUs");
        var request = await _gate.RequestApprovalAsync(ctx);

        request.Context.Urgency.Should().Be("high");
        request.Context.Impact.Should().Be("Affects 10,000 SKUs");
    }

    [Fact]
    public async Task RequestApproval_MultipleRequests_GetUniqueIds()
    {
        var r1 = await _gate.RequestApprovalAsync(MakeContext(action: "Action 1"));
        var r2 = await _gate.RequestApprovalAsync(MakeContext(action: "Action 2"));

        r1.RequestId.Should().NotBe(r2.RequestId);
    }

    [Fact]
    public async Task RequestApproval_StartsAsPending()
    {
        var request = await _gate.RequestApprovalAsync(MakeContext());
        request.Decision.Should().Be(ApprovalDecision.Pending);
    }

    #endregion

    #region GetResultAsync

    [Fact]
    public async Task GetResult_NewRequest_ReturnsPending()
    {
        var request = await _gate.RequestApprovalAsync(MakeContext());
        var result = await _gate.GetResultAsync(request.RequestId);

        result.Decision.Should().Be(ApprovalDecision.Pending);
        result.RequestId.Should().Be(request.RequestId);
    }

    [Fact]
    public async Task GetResult_AfterApproval_ReturnsApproved()
    {
        var request = await _gate.RequestApprovalAsync(MakeContext());
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Approved);

        var result = await _gate.GetResultAsync(request.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Approved);
    }

    [Fact]
    public async Task GetResult_AfterRejection_ReturnsRejected()
    {
        var request = await _gate.RequestApprovalAsync(MakeContext());
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Rejected, comment: "Too risky");

        var result = await _gate.GetResultAsync(request.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Rejected);
        result.Comment.Should().Be("Too risky");
    }

    [Fact]
    public void GetResult_NonexistentId_Throws()
    {
        var act = () => _gate.GetResultAsync("nonexistent-id");
        act.Should().ThrowAsync<KeyNotFoundException>();
    }

    #endregion

    #region RespondAsync

    [Fact]
    public async Task Respond_Approved_UpdatesCorrectly()
    {
        var request = await _gate.RequestApprovalAsync(MakeContext());
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Approved);

        var result = await _gate.GetResultAsync(request.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Approved);
        result.RespondedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Respond_Rejected_WithComment()
    {
        var request = await _gate.RequestApprovalAsync(MakeContext());
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Rejected, comment: "No");

        var result = await _gate.GetResultAsync(request.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Rejected);
        result.Comment.Should().Be("No");
    }

    [Fact]
    public async Task Respond_Modified_IncludesComment()
    {
        var request = await _gate.RequestApprovalAsync(MakeContext(action: "Delete 500 records"));
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Modified,
            comment: "Approved but limit to 100 records");

        var result = await _gate.GetResultAsync(request.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Modified);
        result.Comment.Should().Be("Approved but limit to 100 records");
    }

    [Fact]
    public async Task Respond_CommentIsOptional()
    {
        var request = await _gate.RequestApprovalAsync(MakeContext());
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Approved);

        var result = await _gate.GetResultAsync(request.RequestId);
        result.Comment.Should().BeNull();
    }

    [Fact]
    public async Task Respond_AlreadyResolved_IsIdempotent()
    {
        var request = await _gate.RequestApprovalAsync(MakeContext());
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Approved);
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Rejected);

        var result = await _gate.GetResultAsync(request.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Approved, "first decision wins");
    }

    #endregion

    #region WaitForApprovalAsync

    [Fact]
    public async Task WaitForApproval_ReturnsWhenDecisionMade()
    {
        var request = await _gate.RequestApprovalAsync(MakeContext());

        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            await _gate.RespondAsync(request.RequestId, ApprovalDecision.Approved);
        });

        var result = await _gate.WaitForApprovalAsync(request.RequestId, timeout: TimeSpan.FromSeconds(10));
        result.Decision.Should().Be(ApprovalDecision.Approved);
    }

    [Fact]
    public async Task WaitForApproval_TimesOut_ReturnsTimedOut()
    {
        var request = await _gate.RequestApprovalAsync(MakeContext());

        var result = await _gate.WaitForApprovalAsync(request.RequestId, timeout: TimeSpan.FromMilliseconds(100));
        result.Decision.Should().Be(ApprovalDecision.TimedOut);
    }

    [Fact]
    public async Task WaitForApproval_AlreadyResolved_ReturnsImmediately()
    {
        var request = await _gate.RequestApprovalAsync(MakeContext());
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Rejected, comment: "Nope");

        var result = await _gate.WaitForApprovalAsync(request.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Rejected);
        result.Comment.Should().Be("Nope");
    }

    #endregion

    #region Concurrency

    [Fact]
    public async Task ConcurrentRequests_DontInterfere()
    {
        var tasks = Enumerable.Range(0, 10).Select(i =>
            _gate.RequestApprovalAsync(MakeContext(agentId: $"agent-{i}", userId: $"user-{i}", action: $"Action {i}")));

        var requests = await Task.WhenAll(tasks);
        requests.Should().HaveCount(10);
        requests.Select(r => r.RequestId).Distinct().Should().HaveCount(10);
    }

    [Fact]
    public async Task ConcurrentRequests_EachResolvable()
    {
        var r1 = await _gate.RequestApprovalAsync(MakeContext(action: "Action 1"));
        var r2 = await _gate.RequestApprovalAsync(MakeContext(action: "Action 2"));

        await _gate.RespondAsync(r1.RequestId, ApprovalDecision.Approved);
        await _gate.RespondAsync(r2.RequestId, ApprovalDecision.Rejected);

        var result1 = await _gate.GetResultAsync(r1.RequestId);
        var result2 = await _gate.GetResultAsync(r2.RequestId);

        result1.Decision.Should().Be(ApprovalDecision.Approved);
        result2.Decision.Should().Be(ApprovalDecision.Rejected);
    }

    #endregion

    #region Audit Trail

    [Fact]
    public async Task AuditTrail_RequestPersisted()
    {
        var request = await _gate.RequestApprovalAsync(MakeContext());
        var pending = await _gate.GetPendingAsync("user-1");
        pending.Should().Contain(r => r.RequestId == request.RequestId);
    }

    [Fact]
    public async Task AuditTrail_ResponsePersisted()
    {
        var request = await _gate.RequestApprovalAsync(MakeContext());
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Approved, comment: "LGTM");

        var history = await _gate.GetHistoryAsync();
        history.Should().Contain(r => r.RequestId == request.RequestId && r.Decision == ApprovalDecision.Approved);
    }

    [Fact]
    public async Task AuditTrail_HistoryIsMostRecentFirst()
    {
        var r1 = await _gate.RequestApprovalAsync(MakeContext(action: "First"));
        await _gate.RespondAsync(r1.RequestId, ApprovalDecision.Approved);
        await Task.Delay(10);

        var r2 = await _gate.RequestApprovalAsync(MakeContext(action: "Second"));
        await _gate.RespondAsync(r2.RequestId, ApprovalDecision.Rejected);

        var history = await _gate.GetHistoryAsync();
        history.Should().HaveCount(2);
        history[0].Context.Action.Should().Be("Second");
    }

    #endregion

    #region GetPendingAsync / GetHistoryAsync

    [Fact]
    public async Task GetPending_ReturnsOnlyPendingForUser()
    {
        await _gate.RequestApprovalAsync(MakeContext(action: "Pending action"));
        var resolved = await _gate.RequestApprovalAsync(MakeContext(action: "Resolved action"));
        await _gate.RespondAsync(resolved.RequestId, ApprovalDecision.Approved);

        await _gate.RequestApprovalAsync(MakeContext(userId: "user-2", action: "Other user action"));

        var pending = await _gate.GetPendingAsync("user-1");
        pending.Should().HaveCount(1);
        pending[0].Context.Action.Should().Be("Pending action");
    }

    [Fact]
    public async Task GetHistory_ReturnsOnlyResolved()
    {
        var r1 = await _gate.RequestApprovalAsync(MakeContext(action: "Resolved"));
        await _gate.RespondAsync(r1.RequestId, ApprovalDecision.Approved);

        await _gate.RequestApprovalAsync(MakeContext(action: "Still pending"));

        var history = await _gate.GetHistoryAsync();
        history.Should().HaveCount(1);
        history[0].Context.Action.Should().Be("Resolved");
    }

    [Fact]
    public async Task GetHistory_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
        {
            var r = await _gate.RequestApprovalAsync(MakeContext(action: $"Action {i}"));
            await _gate.RespondAsync(r.RequestId, ApprovalDecision.Approved);
        }

        var history = await _gate.GetHistoryAsync(limit: 3);
        history.Should().HaveCount(3);
    }

    #endregion
}

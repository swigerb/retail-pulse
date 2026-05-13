using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Approval;
using RetailPulse.Contracts.Approval;

namespace RetailPulse.Tests.Approval;

/// <summary>
/// Tests for the Approval REST API contract behavior.
/// Uses the real SqliteApprovalGate to validate the API contract surface
/// that endpoints GET /api/approvals/pending, POST /api/approvals/{id}/respond,
/// and GET /api/approvals/history are built against.
/// </summary>
public class ApprovalApiTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteApprovalGate _gate;

    public ApprovalApiTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"approval_api_test_{Guid.NewGuid():N}.db");
        _gate = new SqliteApprovalGate(_dbPath, Mock.Of<ILogger<SqliteApprovalGate>>());
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private static ApprovalContext MakeContext(
        string userId = "user-1", string action = "Test action")
        => new("agent-1", userId, action, "Low impact", "medium", "Testing");

    #region GET /api/approvals/pending

    [Fact]
    public async Task GetPending_ReturnsOnlyPendingForUser()
    {
        await _gate.RequestApprovalAsync(MakeContext(action: "Pending 1"));
        await _gate.RequestApprovalAsync(MakeContext(action: "Pending 2"));

        var resolved = await _gate.RequestApprovalAsync(MakeContext(action: "Resolved"));
        await _gate.RespondAsync(resolved.RequestId, ApprovalDecision.Approved);

        await _gate.RequestApprovalAsync(MakeContext(userId: "user-2", action: "Other user"));

        var pending = await _gate.GetPendingAsync("user-1");

        pending.Should().HaveCount(2);
        pending.Should().OnlyContain(r => r.Context.UserId == "user-1");
        pending.Should().NotContain(r => r.Context.Action == "Resolved");
    }

    [Fact]
    public async Task GetPending_EmptyForNewUser()
    {
        var pending = await _gate.GetPendingAsync("nobody");
        pending.Should().NotBeNull();
        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPending_IncludesUrgencyAndImpact()
    {
        await _gate.RequestApprovalAsync(
            new ApprovalContext("agent-1", "user-1", "Critical action", "Revenue impact", "high", "Urgent"));

        var pending = await _gate.GetPendingAsync("user-1");

        pending.Should().ContainSingle();
        pending[0].Context.Urgency.Should().Be("high");
        pending[0].Context.Impact.Should().Be("Revenue impact");
    }

    #endregion

    #region POST /api/approvals/{id}/respond

    [Fact]
    public async Task PostRespond_ApprovedDecision_Updates()
    {
        var request = await _gate.RequestApprovalAsync(MakeContext());
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Approved, "Approved by manager");

        var result = await _gate.GetResultAsync(request.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Approved);
        result.Comment.Should().Be("Approved by manager");
    }

    [Fact]
    public async Task PostRespond_InvalidRequestId_Throws()
    {
        var act = () => _gate.GetResultAsync("nonexistent-id");
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task PostRespond_AlreadyResolved_SilentlyIgnored()
    {
        var request = await _gate.RequestApprovalAsync(MakeContext());
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Approved);

        // Second respond is silently ignored (0 rows updated)
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Rejected);

        var result = await _gate.GetResultAsync(request.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Approved, "first decision wins");
    }

    [Fact]
    public async Task PostRespond_ModifiedDecision_IncludesExplanation()
    {
        var request = await _gate.RequestApprovalAsync(MakeContext(action: "Update all prices"));
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Modified,
            "Only update Southwest region");

        var result = await _gate.GetResultAsync(request.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Modified);
        result.Comment.Should().Be("Only update Southwest region");
    }

    #endregion

    #region GET /api/approvals/history

    [Fact]
    public async Task GetHistory_ReturnsResolvedRequests()
    {
        var r1 = await _gate.RequestApprovalAsync(MakeContext(action: "First action"));
        await _gate.RespondAsync(r1.RequestId, ApprovalDecision.Approved);
        await Task.Delay(10);

        var r2 = await _gate.RequestApprovalAsync(MakeContext(action: "Second action"));
        await _gate.RespondAsync(r2.RequestId, ApprovalDecision.Rejected, "Too risky");
        await Task.Delay(10);

        var r3 = await _gate.RequestApprovalAsync(MakeContext(action: "Third action"));
        await _gate.RespondAsync(r3.RequestId, ApprovalDecision.Modified, "Scale down");

        var history = await _gate.GetHistoryAsync();
        history.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetHistory_MostRecentFirst()
    {
        var r1 = await _gate.RequestApprovalAsync(MakeContext(action: "Old"));
        await _gate.RespondAsync(r1.RequestId, ApprovalDecision.Approved);
        await Task.Delay(50);

        var r2 = await _gate.RequestApprovalAsync(MakeContext(action: "New"));
        await _gate.RespondAsync(r2.RequestId, ApprovalDecision.Rejected);

        var history = await _gate.GetHistoryAsync();
        history[0].Context.Action.Should().Be("New");
    }

    [Fact]
    public async Task GetHistory_EmptyForFreshDb()
    {
        var history = await _gate.GetHistoryAsync();
        history.Should().NotBeNull();
        history.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHistory_ExcludesPendingRequests()
    {
        var r1 = await _gate.RequestApprovalAsync(MakeContext(action: "Resolved"));
        await _gate.RespondAsync(r1.RequestId, ApprovalDecision.Approved);

        await _gate.RequestApprovalAsync(MakeContext(action: "Still pending"));

        var history = await _gate.GetHistoryAsync();
        history.Should().HaveCount(1);
        history[0].Decision.Should().Be(ApprovalDecision.Approved);
    }

    [Fact]
    public async Task GetHistory_DoesNotLeakBetweenUsers()
    {
        var r1 = await _gate.RequestApprovalAsync(MakeContext(userId: "user-1", action: "U1 action"));
        await _gate.RespondAsync(r1.RequestId, ApprovalDecision.Approved);

        var r2 = await _gate.RequestApprovalAsync(MakeContext(userId: "user-2", action: "U2 action"));
        await _gate.RespondAsync(r2.RequestId, ApprovalDecision.Rejected);

        // GetHistoryAsync returns ALL users' history (it's a global audit trail)
        var history = await _gate.GetHistoryAsync();
        history.Should().HaveCount(2);
    }

    #endregion

    #region Full Flow

    [Fact]
    public async Task FullApprovalFlow_RequestRespondVerify()
    {
        var request = await _gate.RequestApprovalAsync(
            new ApprovalContext("demand-agent", "user-1",
                "Generate 90-day forecast for all brands",
                "High compute cost", "medium", "Data refresh needed"));

        // Verify it appears in pending
        var pending = await _gate.GetPendingAsync("user-1");
        pending.Should().ContainSingle(r => r.RequestId == request.RequestId);

        // Respond
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Approved,
            "Go ahead, off-peak hours");

        // Verify it's no longer pending
        pending = await _gate.GetPendingAsync("user-1");
        pending.Should().NotContain(r => r.RequestId == request.RequestId);

        // Verify it appears in history
        var history = await _gate.GetHistoryAsync();
        history.Should().ContainSingle(r => r.RequestId == request.RequestId);
        history[0].Decision.Should().Be(ApprovalDecision.Approved);
        history[0].Comment.Should().Be("Go ahead, off-peak hours");
    }

    #endregion
}

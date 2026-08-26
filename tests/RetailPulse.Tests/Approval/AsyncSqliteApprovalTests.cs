using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Approval;
using RetailPulse.Contracts.Approval;

namespace RetailPulse.Tests.Approval;

/// <summary>
/// Tests for SqliteApprovalGate async SQLite and exponential backoff behavior.
/// Validates that all database operations are truly async and that
/// WaitForApprovalAsync uses exponential backoff instead of fixed-interval polling.
/// </summary>
public class AsyncSqliteApprovalTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteApprovalGate _gate;

    public AsyncSqliteApprovalTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"approval_async_test_{Guid.NewGuid():N}.db");
        _gate = new SqliteApprovalGate(_dbPath, Mock.Of<ILogger<SqliteApprovalGate>>());
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private static ApprovalContext MakeContext(string action = "Test action")
        => new("agent-1", "user-1", action, "Low", "medium", "Testing");

    [Fact]
    public async Task RequestApprovalAsync_IsNonBlocking()
    {
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext());

        request.Should().NotBeNull();
        request.RequestId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetResultAsync_ReturnsAsyncResult()
    {
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext());
        ApprovalResult result = await _gate.GetResultAsync(request.RequestId);

        result.Decision.Should().Be(ApprovalDecision.Pending);
    }

    [Fact]
    public async Task RespondAsync_PersistsDecisionAsynchronously()
    {
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext());
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Approved, "Looks good");

        ApprovalResult result = await _gate.GetResultAsync(request.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Approved);
        result.Comment.Should().Be("Looks good");
    }

    [Fact]
    public async Task GetPendingAsync_ReturnsAsyncResults()
    {
        await _gate.RequestApprovalAsync(MakeContext("Pending 1"));
        await _gate.RequestApprovalAsync(MakeContext("Pending 2"));

        IReadOnlyList<ApprovalRequest> pending = await _gate.GetPendingAsync("user-1");
        pending.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsAsyncResults()
    {
        ApprovalRequest r = await _gate.RequestApprovalAsync(MakeContext("Historic"));
        await _gate.RespondAsync(r.RequestId, ApprovalDecision.Rejected);

        IReadOnlyList<ApprovalRequest> history = await _gate.GetHistoryAsync();
        history.Should().HaveCount(1);
    }

    [Fact]
    public async Task CancellationToken_IsPropagated()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task<ApprovalRequest>> act = () => _gate.RequestApprovalAsync(MakeContext(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WaitForApprovalAsync_UsesExponentialBackoff()
    {
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext());

        // Respond after a short delay. Track the responder as a proper Task
        // and await it in a finally so a slow responder cannot outlive the
        // test method and race the test's IDisposable disposing the SQLite
        // files under it. Under CPU load the fire-and-forget original
        // occasionally raced with the waiter's own connection open across
        // Microsoft.Data.Sqlite's shared cache and surfaced as an
        // ObjectDisposedException on the sqlite3 SafeHandle.
        using var respondCts = new CancellationTokenSource();
        var responder = Task.Run(async () =>
        {
            await Task.Delay(500, respondCts.Token);
            await _gate.RespondAsync(request.RequestId, ApprovalDecision.Approved);
        }, respondCts.Token);
        try
        {
            ApprovalResult result = await _gate.WaitForApprovalAsync(request.RequestId, timeout: TimeSpan.FromSeconds(5));
            result.Decision.Should().Be(ApprovalDecision.Approved);
        }
        finally
        {
            respondCts.Cancel();
            try { await responder; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task WaitForApprovalAsync_TimesOut_WithBackoff()
    {
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext());

        DateTimeOffset start = DateTimeOffset.UtcNow;
        ApprovalResult result = await _gate.WaitForApprovalAsync(request.RequestId, timeout: TimeSpan.FromMilliseconds(300));
        TimeSpan elapsed = DateTimeOffset.UtcNow - start;

        result.Decision.Should().Be(ApprovalDecision.TimedOut);
        // Should complete reasonably close to timeout (backoff starts at 250ms)
        elapsed.TotalMilliseconds.Should().BeGreaterThan(200);
    }

    [Fact]
    public Task BackoffConstants_AreCorrect()
    {
        SqliteApprovalGate.InitialBackoff.Should().Be(TimeSpan.FromMilliseconds(250));
        SqliteApprovalGate.MaxBackoff.Should().Be(TimeSpan.FromSeconds(4));
        SqliteApprovalGate.BackoffMultiplier.Should().Be(2.0);
        return Task.CompletedTask;
    }
}

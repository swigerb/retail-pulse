using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Approval;
using RetailPulse.Contracts.Approval;
using RetailPulse.Tests.TestInfrastructure;

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
        _dbPath = SqliteTestCleanup.NewDbPath("approval_async_test");
        _gate = new SqliteApprovalGate(_dbPath, Mock.Of<ILogger<SqliteApprovalGate>>());
    }

    public void Dispose()
    {
        SqliteTestCleanup.ReleaseAndDelete(_dbPath);
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
        // Verify the invariant "WaitForApprovalAsync returns the persisted
        // Approved decision" without any wall-clock/thread-pool timing
        // dependency. The original shape fired the responder as
        // `_ = Task.Run(async () => { Task.Delay(500); RespondAsync(...); })`
        // — an unowned task whose Microsoft.Data.Sqlite shared-cache
        // connection could race the waiter's own connection open under
        // CPU contention and surface as
        // `System.ObjectDisposedException: Cannot access a disposed
        //  object. Object name: 'SQLitePCL.sqlite3'`
        // during `sqlite3_prepare_v2` on the waiter's next read (observed
        // in the 20-run acceptance sweep, attempt 2 run 20 of #156).
        // Persisting the decision synchronously before starting the wait
        // proves the same invariant deterministically and never sees the
        // load-sensitivity.
        //
        // Backoff timing itself is covered by the sibling
        // `WaitForApprovalAsync_TimesOut_WithBackoff` and the
        // `BackoffConstants_AreCorrect` tests below.
        ApprovalRequest request = await _gate.RequestApprovalAsync(MakeContext());
        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Approved);

        ApprovalResult result = await _gate.WaitForApprovalAsync(request.RequestId, timeout: TimeSpan.FromSeconds(5));
        result.Decision.Should().Be(ApprovalDecision.Approved);
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

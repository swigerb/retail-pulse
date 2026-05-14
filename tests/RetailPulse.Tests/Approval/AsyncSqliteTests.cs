using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Approval;
using RetailPulse.Api.Memory;
using RetailPulse.Contracts.Approval;
using RetailPulse.Contracts.Memory;

namespace RetailPulse.Tests.Approval;

/// <summary>
/// Sprint 3 reliability: async SQLite with CancellationToken.
/// Validates that the approval store and memory store respect cancellation,
/// handle concurrent access without deadlocks, and complete async operations.
/// </summary>
public class AsyncSqliteTests : IDisposable
{
    private readonly string _approvalDbPath;
    private readonly string _memoryDbPath;
    private readonly SqliteApprovalGate _gate;
    private readonly SqliteConversationMemory _memory;

    public AsyncSqliteTests()
    {
        _approvalDbPath = Path.Combine(Path.GetTempPath(), $"async_approval_{Guid.NewGuid():N}.db");
        _memoryDbPath = Path.Combine(Path.GetTempPath(), $"async_memory_{Guid.NewGuid():N}.db");
        _gate = new SqliteApprovalGate(_approvalDbPath, Mock.Of<ILogger<SqliteApprovalGate>>());
        _memory = new SqliteConversationMemory(_memoryDbPath, Mock.Of<ILogger<SqliteConversationMemory>>());
    }

    public void Dispose()
    {
        _memory.Dispose();
        foreach (var path in new[] { _approvalDbPath, _memoryDbPath })
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(path + "-wal"); } catch { }
            try { File.Delete(path + "-shm"); } catch { }
        }
    }

    private static ApprovalContext MakeContext(string userId = "user-1") =>
        new("agent-1", userId, "Test action", "Low impact", "medium", "Testing");

    private static MemoryEntry MakeMemoryEntry(string userId = "user-1") =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            UserId: userId,
            Type: MemoryType.ConversationSummary,
            Content: "Test memory content",
            EntityKey: null,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(30));

    // ── CancellationToken Respected ─────────────────────────────────────

    [Fact]
    public async Task ApprovalWait_CancelledToken_ThrowsOperationCanceledException()
    {
        var request = await _gate.RequestApprovalAsync(MakeContext());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _gate.WaitForApprovalAsync(request.RequestId, TimeSpan.FromSeconds(30), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task MemoryStore_CancelledToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _memory.StoreAsync("user-1", MakeMemoryEntry(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task MemoryRecall_CancelledToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _memory.RecallAsync("user-1", "test query", ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task MemoryForget_CancelledToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _memory.ForgetAsync("user-1", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Concurrent Access ───────────────────────────────────────────────

    [Fact]
    public async Task ApprovalStore_ConcurrentRequests_DoNotDeadlock()
    {
        var tasks = Enumerable.Range(0, 20)
            .Select(i => _gate.RequestApprovalAsync(MakeContext($"user-{i}")))
            .ToArray();

        var completed = await Task.WhenAll(tasks);

        completed.Should().HaveCount(20);
        completed.Select(r => r.RequestId).Distinct().Should().HaveCount(20);
    }

    [Fact]
    public async Task MemoryStore_ConcurrentWrites_DoNotDeadlock()
    {
        var tasks = Enumerable.Range(0, 20)
            .Select(i => _memory.StoreAsync($"user-{i}", MakeMemoryEntry($"user-{i}")))
            .ToArray();

        await Task.WhenAll(tasks);

        // Verify all writes succeeded by recalling for each user
        for (int i = 0; i < 20; i++)
        {
            var recalled = await _memory.RecallAsync($"user-{i}");
            recalled.Should().HaveCount(1);
        }
    }

    [Fact]
    public async Task ConcurrentReadWrite_DoesNotDeadlock()
    {
        // Pre-populate
        for (int i = 0; i < 10; i++)
            await _memory.StoreAsync("shared-user", MakeMemoryEntry("shared-user"));

        // Mix reads and writes concurrently
        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(_memory.StoreAsync("shared-user", MakeMemoryEntry("shared-user")));
            tasks.Add(_memory.RecallAsync("shared-user", "test"));
        }

        var allTasks = Task.WhenAll(tasks);
        var completedInTime = await Task.WhenAny(allTasks, Task.Delay(TimeSpan.FromSeconds(10)));

        completedInTime.Should().Be(allTasks, "concurrent read/write should not deadlock");
    }

    // ── Async Operations Complete Successfully ──────────────────────────

    [Fact]
    public async Task ApprovalRequestAndRespond_CompletesSuccessfully()
    {
        var request = await _gate.RequestApprovalAsync(MakeContext());
        request.Decision.Should().Be(ApprovalDecision.Pending);

        await _gate.RespondAsync(request.RequestId, ApprovalDecision.Approved, "Looks good");

        var result = await _gate.GetResultAsync(request.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Approved);
        result.Comment.Should().Be("Looks good");
    }

    [Fact]
    public async Task MemoryStoreAndRecall_CompletesSuccessfully()
    {
        var entry = MakeMemoryEntry();
        await _memory.StoreAsync("user-1", entry);

        var recalled = await _memory.RecallAsync("user-1");

        recalled.Should().HaveCount(1);
        recalled[0].Content.Should().Be("Test memory content");
    }

    [Fact]
    public async Task MemoryForgetAndRecall_ReturnsEmpty()
    {
        await _memory.StoreAsync("user-1", MakeMemoryEntry());
        await _memory.ForgetAsync("user-1");

        var recalled = await _memory.RecallAsync("user-1");

        recalled.Should().BeEmpty();
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Resilience;

namespace RetailPulse.Tests.Chaos;

/// <summary>
/// Tests for DeadLetterQueue — enqueue, get pending, replay, and persistence.
/// </summary>
public class DeadLetterQueueTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DeadLetterQueue _queue;

    public DeadLetterQueueTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"dlq-test-{Guid.NewGuid()}.db");
        _queue = new DeadLetterQueue(NullLogger<DeadLetterQueue>.Instance, _dbPath);
    }

    [Fact]
    public async Task Enqueue_PersistsEntry()
    {
        await _queue.EnqueueAsync("test-operation", "{\"key\":\"value\"}", "Something failed");

        var pending = await _queue.GetPendingAsync();
        pending.Should().HaveCount(1);
        pending[0].Operation.Should().Be("test-operation");
        pending[0].Error.Should().Be("Something failed");
        pending[0].Payload.Should().Be("{\"key\":\"value\"}");
    }

    [Fact]
    public async Task GetPendingCount_ReturnsCorrectCount()
    {
        await _queue.EnqueueAsync("op1", null, "err1");
        await _queue.EnqueueAsync("op2", null, "err2");
        await _queue.EnqueueAsync("op3", null, "err3");

        var count = await _queue.GetPendingCountAsync();
        count.Should().Be(3);
    }

    [Fact]
    public async Task MarkReplayed_RemovesFromPending()
    {
        await _queue.EnqueueAsync("op1", null, "err1");
        var pending = await _queue.GetPendingAsync();
        pending.Should().HaveCount(1);

        await _queue.MarkReplayedAsync(pending[0].Id);

        var afterReplay = await _queue.GetPendingAsync();
        afterReplay.Should().BeEmpty();
    }

    [Fact]
    public async Task MarkFailed_IncrementsRetryCount()
    {
        await _queue.EnqueueAsync("op1", null, "err1");
        var pending = await _queue.GetPendingAsync();

        await _queue.MarkFailedAsync(pending[0].Id);
        await _queue.MarkFailedAsync(pending[0].Id);

        var updated = await _queue.GetPendingAsync();
        updated[0].RetryCount.Should().Be(2);
    }

    [Fact]
    public async Task Enqueue_WithNullPayload_Works()
    {
        await _queue.EnqueueAsync("null-payload-op", null, "error msg");

        var pending = await _queue.GetPendingAsync();
        pending.Should().HaveCount(1);
        pending[0].Payload.Should().BeNull();
    }

    [Fact]
    public async Task GetPending_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
            await _queue.EnqueueAsync($"op-{i}", null, "err");

        var limited = await _queue.GetPendingAsync(limit: 3);
        limited.Should().HaveCount(3);
    }

    public void Dispose()
    {
        _queue.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}

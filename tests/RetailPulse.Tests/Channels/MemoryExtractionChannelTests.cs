using FluentAssertions;
using RetailPulse.Api.Memory;

namespace RetailPulse.Tests.Channels;

/// <summary>
/// Tests for MemoryExtractionChannel — bounded channel with drop counter for observability.
/// </summary>
public class MemoryExtractionChannelTests
{
    [Fact]
    public Task TryWrite_WithinCapacity_ReturnsTrue()
    {
        var channel = new MemoryExtractionChannel();

        var result = channel.TryWrite(new MemoryWorkItem("user-1", "hello", "world"));

        result.Should().BeTrue();
        channel.DroppedCount.Should().Be(0);
        return Task.CompletedTask;
    }

    [Fact]
    public Task TryWrite_WhenFull_ReturnsFalse_IncrementsDroppedCount()
    {
        var channel = new MemoryExtractionChannel(capacity: 2);

        channel.TryWrite(new MemoryWorkItem("u1", "m1", "r1")).Should().BeTrue();
        channel.TryWrite(new MemoryWorkItem("u2", "m2", "r2")).Should().BeTrue();
        channel.TryWrite(new MemoryWorkItem("u3", "m3", "r3")).Should().BeFalse();

        channel.DroppedCount.Should().Be(1);
        return Task.CompletedTask;
    }

    [Fact]
    public Task DroppedCount_AccumulatesAcrossMultipleDrops()
    {
        var channel = new MemoryExtractionChannel(capacity: 1);

        channel.TryWrite(new MemoryWorkItem("u1", "m1", "r1"));

        for (int i = 0; i < 5; i++)
            channel.TryWrite(new MemoryWorkItem($"u{i}", $"m{i}", $"r{i}"));

        channel.DroppedCount.Should().Be(5);
        return Task.CompletedTask;
    }

    [Fact]
    public Task DefaultCapacity_Is1000()
    {
        MemoryExtractionChannel.DefaultCapacity.Should().Be(1000);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Reader_ReturnsItemsInOrder()
    {
        var channel = new MemoryExtractionChannel();

        channel.TryWrite(new MemoryWorkItem("user-1", "first", "r1"));
        channel.TryWrite(new MemoryWorkItem("user-2", "second", "r2"));
        channel.Complete();

        var items = new List<MemoryWorkItem>();
        await foreach (var item in channel.Reader.ReadAllAsync())
            items.Add(item);

        items.Should().HaveCount(2);
        items[0].UserMessage.Should().Be("first");
        items[1].UserMessage.Should().Be("second");
    }

    [Fact]
    public async Task TryWrite_IncludesTraceMetadata()
    {
        var channel = new MemoryExtractionChannel();
        var item = new MemoryWorkItem("user-1", "msg", "reply", TraceId: "trace-123", ParentSpanId: "span-456");

        channel.TryWrite(item).Should().BeTrue();
        channel.Complete();

        var read = await channel.Reader.ReadAsync();
        read.TraceId.Should().Be("trace-123");
        read.ParentSpanId.Should().Be("span-456");
    }

    [Fact]
    public async Task ConcurrentWrites_DroppedCountIsThreadSafe()
    {
        var channel = new MemoryExtractionChannel(capacity: 5);

        // Fill channel
        for (int i = 0; i < 5; i++)
            channel.TryWrite(new MemoryWorkItem($"fill-{i}", "m", "r"));

        // Concurrent overflow writes
        var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(() =>
            channel.TryWrite(new MemoryWorkItem($"overflow-{i}", "m", "r"))
        ));
        await Task.WhenAll(tasks);

        channel.DroppedCount.Should().Be(100);
    }
}

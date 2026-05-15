using FluentAssertions;
using RetailPulse.Api.Tracing;
using RetailPulse.Contracts.Tracing;

namespace RetailPulse.Tests.Channels;

/// <summary>
/// Tests for TelemetryPushChannel — bounded channel for SignalR backpressure with dropped-item counter.
/// </summary>
public class TelemetryPushChannelTests
{
    [Fact]
    public Task TryWrite_WithinCapacity_ReturnsTrue()
    {
        var channel = new TelemetryPushChannel();
        var item = new TelemetryPushItem("trace_started", TraceId: "t1", Timestamp: DateTimeOffset.UtcNow);

        channel.TryWrite(item).Should().BeTrue();
        channel.DroppedCount.Should().Be(0);
        return Task.CompletedTask;
    }

    [Fact]
    public Task TryWrite_WhenFull_ReturnsFalse_IncrementsDropped()
    {
        var channel = new TelemetryPushChannel(capacity: 2);

        channel.TryWrite(new TelemetryPushItem("trace_started", TraceId: "t1")).Should().BeTrue();
        channel.TryWrite(new TelemetryPushItem("trace_started", TraceId: "t2")).Should().BeTrue();
        channel.TryWrite(new TelemetryPushItem("trace_started", TraceId: "t3")).Should().BeFalse();

        channel.DroppedCount.Should().Be(1);
        return Task.CompletedTask;
    }

    [Fact]
    public Task DroppedCount_AccumulatesCorrectly()
    {
        var channel = new TelemetryPushChannel(capacity: 1);
        channel.TryWrite(new TelemetryPushItem("trace_started", TraceId: "t1"));

        for (int i = 0; i < 10; i++)
            channel.TryWrite(new TelemetryPushItem("span_completed"));

        channel.DroppedCount.Should().Be(10);
        return Task.CompletedTask;
    }

    [Fact]
    public Task DefaultCapacity_Is1000()
    {
        TelemetryPushChannel.DefaultCapacity.Should().Be(1000);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SpanCompletedItems_IncludeSpanData()
    {
        var channel = new TelemetryPushChannel();
        var span = new TraceSpan("s1", "t1", null, "test.op", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 42.0);
        var item = new TelemetryPushItem("span_completed", Span: span);

        channel.TryWrite(item).Should().BeTrue();
        channel.Complete();

        var read = await channel.Reader.ReadAsync();
        read.EventType.Should().Be("span_completed");
        read.Span.Should().NotBeNull();
        read.Span.OperationName.Should().Be("test.op");
    }

    [Fact]
    public async Task Reader_ReturnsItemsInFifoOrder()
    {
        var channel = new TelemetryPushChannel();

        channel.TryWrite(new TelemetryPushItem("trace_started", TraceId: "first"));
        channel.TryWrite(new TelemetryPushItem("trace_started", TraceId: "second"));
        channel.Complete();

        var items = new List<TelemetryPushItem>();
        await foreach (var item in channel.Reader.ReadAllAsync())
            items.Add(item);

        items[0].TraceId.Should().Be("first");
        items[1].TraceId.Should().Be("second");
    }

    [Fact]
    public async Task ConcurrentWrites_DroppedCountIsThreadSafe()
    {
        var channel = new TelemetryPushChannel(capacity: 3);
        for (int i = 0; i < 3; i++)
            channel.TryWrite(new TelemetryPushItem("trace_started", TraceId: $"fill-{i}"));

        var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(() =>
            channel.TryWrite(new TelemetryPushItem("span_completed"))
        ));
        await Task.WhenAll(tasks);

        channel.DroppedCount.Should().Be(50);
    }
}

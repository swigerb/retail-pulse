using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Moq;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Tracing;
using RetailPulse.Contracts.Tracing;

namespace RetailPulse.Tests.Tracing;

/// <summary>
/// Tests for InMemoryTraceCollector with bounded-channel SignalR backpressure.
/// Validates that span capture uses the TelemetryPushChannel instead of fire-and-forget.
/// </summary>
public class TraceCollectorBackpressureTests
{
    [Fact]
    public async Task CaptureSpan_WritesToTelemetryPushChannel()
    {
        var pushChannel = new TelemetryPushChannel();
        var collector = CreateCollectorWithChannel(pushChannel);

        var span = MakeSpan("trace-1", "op.test");
        collector.CaptureSpan(span);

        pushChannel.Complete();

        var items = new List<TelemetryPushItem>();
        await foreach (var item in pushChannel.Reader.ReadAllAsync())
            items.Add(item);

        // Should have trace_started + span_completed
        items.Should().HaveCount(2);
        items[0].EventType.Should().Be("trace_started");
        items[0].TraceId.Should().Be("trace-1");
        items[1].EventType.Should().Be("span_completed");
        items[1].Span.Should().NotBeNull();
        items[1].Span!.OperationName.Should().Be("op.test");
    }

    [Fact]
    public async Task CaptureSpan_SameTrace_OnlyOneTraceStarted()
    {
        var pushChannel = new TelemetryPushChannel();
        var collector = CreateCollectorWithChannel(pushChannel);

        collector.CaptureSpan(MakeSpan("trace-1", "op.first"));
        collector.CaptureSpan(MakeSpan("trace-1", "op.second"));

        pushChannel.Complete();

        var items = new List<TelemetryPushItem>();
        await foreach (var item in pushChannel.Reader.ReadAllAsync())
            items.Add(item);

        // 1 trace_started + 2 span_completed
        items.Count(i => i.EventType == "trace_started").Should().Be(1);
        items.Count(i => i.EventType == "span_completed").Should().Be(2);
    }

    [Fact]
    public async Task CaptureSpan_WhenChannelFull_DoesNotThrow()
    {
        var pushChannel = new TelemetryPushChannel(capacity: 1);
        var collector = CreateCollectorWithChannel(pushChannel);

        // This fills the channel (trace_started), then span_completed should be dropped
        var act = () => collector.CaptureSpan(MakeSpan("trace-1", "op.test"));
        act.Should().NotThrow();

        // Second span — more drops but no crash
        act = () => collector.CaptureSpan(MakeSpan("trace-2", "op.test2"));
        act.Should().NotThrow();

        pushChannel.DroppedCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task CollectorWithoutChannel_StillCapturesSpans()
    {
        var collector = new InMemoryTraceCollector();

        collector.CaptureSpan(MakeSpan("trace-1", "op.test"));

        var spans = collector.GetSpans("trace-1");
        spans.Should().NotBeNull();
        spans.Should().HaveCount(1);
    }

    [Fact]
    public async Task DroppedTelemetryCount_TracksDroppedItems()
    {
        var pushChannel = new TelemetryPushChannel(capacity: 2);
        var collector = CreateCollectorWithChannel(pushChannel);

        // Each CaptureSpan for a new trace produces 2 items (trace_started + span_completed)
        // With capacity 2, the first call fills the channel, subsequent calls drop
        collector.CaptureSpan(MakeSpan("t1", "op.1"));
        collector.CaptureSpan(MakeSpan("t2", "op.2"));
        collector.CaptureSpan(MakeSpan("t3", "op.3"));

        pushChannel.DroppedCount.Should().BeGreaterThan(0, "telemetry should be dropped when channel is full");
    }

    private static InMemoryTraceCollector CreateCollectorWithChannel(TelemetryPushChannel channel)
    {
        var mockHubContext = new Mock<IHubContext<TelemetryHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.All).Returns(mockProxy.Object);
        mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        var mockConfig = new Mock<Microsoft.Extensions.Configuration.IConfiguration>();

        return new InMemoryTraceCollector(mockHubContext.Object, mockConfig.Object, channel);
    }

    private static TraceSpan MakeSpan(string traceId, string operation)
    {
        var now = DateTimeOffset.UtcNow;
        return new TraceSpan(
            SpanId: Guid.NewGuid().ToString("N")[..16],
            TraceId: traceId,
            ParentSpanId: null,
            OperationName: operation,
            StartTime: now,
            EndTime: now.AddMilliseconds(50),
            DurationMs: 50);
    }
}

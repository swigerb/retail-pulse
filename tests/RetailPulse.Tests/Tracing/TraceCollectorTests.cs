using FluentAssertions;
using RetailPulse.Api.Tracing;
using RetailPulse.Contracts.Tracing;

namespace RetailPulse.Tests.Tracing;

/// <summary>
/// Tests for ITraceCollector / InMemoryTraceCollector.
/// Covers: span capture, ring buffer eviction, query by traceId,
///         recent traces, concurrency, parent-child relationships.
/// 12+ tests.
/// </summary>
public class TraceCollectorTests
{
    private static TraceSpan MakeSpan(
        string traceId,
        string operationName,
        string? parentSpanId = null,
        int inputTokens = 0,
        int outputTokens = 0,
        decimal cost = 0m,
        double durationMs = 100,
        DateTimeOffset? startTime = null)
    {
        var start = startTime ?? DateTimeOffset.UtcNow;
        return new TraceSpan(
            SpanId: Guid.NewGuid().ToString("N"),
            TraceId: traceId,
            ParentSpanId: parentSpanId,
            OperationName: operationName,
            StartTime: start,
            EndTime: start.AddMilliseconds(durationMs),
            DurationMs: durationMs,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            EstimatedCostUsd: cost
        );
    }

    #region Span Capture

    [Fact]
    public void CaptureSpan_StoresSpanSuccessfully()
    {
        var collector = new InMemoryTraceCollector();
        var span = MakeSpan("trace-1", "agent.routing");

        collector.CaptureSpan(span);

        collector.TraceCount.Should().Be(1);
    }

    [Fact]
    public void CaptureSpan_MultipleSpansSameTrace_GroupedTogether()
    {
        var collector = new InMemoryTraceCollector();
        collector.CaptureSpan(MakeSpan("trace-1", "agent.routing"));
        collector.CaptureSpan(MakeSpan("trace-1", "tool.call"));
        collector.CaptureSpan(MakeSpan("trace-1", "agent.response"));

        var spans = collector.GetSpans("trace-1");

        spans.Should().HaveCount(3);
        collector.TraceCount.Should().Be(1, "all belong to same trace");
    }

    [Fact]
    public void CaptureSpan_DifferentTraces_StoredSeparately()
    {
        var collector = new InMemoryTraceCollector();
        collector.CaptureSpan(MakeSpan("trace-1", "op-a"));
        collector.CaptureSpan(MakeSpan("trace-2", "op-b"));

        collector.TraceCount.Should().Be(2);
        collector.GetSpans("trace-1").Should().ContainSingle();
        collector.GetSpans("trace-2").Should().ContainSingle();
    }

    [Fact]
    public void CaptureSpan_NullSpan_ThrowsArgumentNullException()
    {
        var collector = new InMemoryTraceCollector();

        var act = () => collector.CaptureSpan(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Ring Buffer Eviction

    [Fact]
    public void RingBuffer_EvictsOldestWhenFull()
    {
        var collector = new InMemoryTraceCollector(capacity: 3);

        collector.CaptureSpan(MakeSpan("trace-1", "op-1"));
        collector.CaptureSpan(MakeSpan("trace-2", "op-2"));
        collector.CaptureSpan(MakeSpan("trace-3", "op-3"));
        collector.CaptureSpan(MakeSpan("trace-4", "op-4")); // should evict trace-1

        collector.TraceCount.Should().Be(3);
        collector.GetSpans("trace-1").Should().BeNull("trace-1 was evicted");
        collector.GetSpans("trace-4").Should().NotBeNull();
    }

    [Fact]
    public void RingBuffer_DefaultCapacity100()
    {
        var collector = new InMemoryTraceCollector();

        collector.Capacity.Should().Be(100);
    }

    [Fact]
    public void RingBuffer_EvictsMultipleOldTracesIfNeeded()
    {
        var collector = new InMemoryTraceCollector(capacity: 2);

        collector.CaptureSpan(MakeSpan("trace-1", "op-1"));
        collector.CaptureSpan(MakeSpan("trace-2", "op-2"));
        collector.CaptureSpan(MakeSpan("trace-3", "op-3"));
        collector.CaptureSpan(MakeSpan("trace-4", "op-4"));

        collector.TraceCount.Should().BeLessThanOrEqualTo(2);
        collector.GetSpans("trace-1").Should().BeNull();
        collector.GetSpans("trace-2").Should().BeNull();
    }

    #endregion

    #region Query by TraceId

    [Fact]
    public void GetSpans_ExistingTrace_ReturnsOrderedByStartTime()
    {
        var collector = new InMemoryTraceCollector();
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddMilliseconds(100);
        var t3 = t1.AddMilliseconds(200);

        collector.CaptureSpan(MakeSpan("trace-1", "step-3", startTime: t3));
        collector.CaptureSpan(MakeSpan("trace-1", "step-1", startTime: t1));
        collector.CaptureSpan(MakeSpan("trace-1", "step-2", startTime: t2));

        var spans = collector.GetSpans("trace-1")!;

        spans.Should().HaveCount(3);
        spans[0].OperationName.Should().Be("step-1");
        spans[1].OperationName.Should().Be("step-2");
        spans[2].OperationName.Should().Be("step-3");
    }

    [Fact]
    public void GetSpans_UnknownTraceId_ReturnsNull()
    {
        var collector = new InMemoryTraceCollector();

        collector.GetSpans("nonexistent").Should().BeNull();
    }

    #endregion

    #region Recent Traces

    [Fact]
    public void GetRecentTraces_ReturnsLast20OrderedByTime()
    {
        var collector = new InMemoryTraceCollector();

        for (int i = 0; i < 30; i++)
        {
            collector.CaptureSpan(MakeSpan($"trace-{i:D2}", $"op-{i}",
                startTime: DateTimeOffset.UtcNow.AddSeconds(i)));
        }

        var recent = collector.GetRecentTraces(20);

        recent.Should().HaveCount(20);
        // Most recent first
        recent[0].TraceId.Should().Be("trace-29");
    }

    [Fact]
    public void GetRecentTraces_FewerThanRequested_ReturnsAll()
    {
        var collector = new InMemoryTraceCollector();
        collector.CaptureSpan(MakeSpan("trace-1", "op-1"));
        collector.CaptureSpan(MakeSpan("trace-2", "op-2"));

        var recent = collector.GetRecentTraces(20);

        recent.Should().HaveCount(2);
    }

    #endregion

    #region Concurrency

    [Fact]
    public async Task ConcurrentCapture_NoDataLoss()
    {
        var collector = new InMemoryTraceCollector(capacity: 200);
        var tasks = Enumerable.Range(0, 100).Select(i =>
            Task.Run(() => collector.CaptureSpan(MakeSpan($"trace-{i}", $"op-{i}")))
        ).ToArray();

        await Task.WhenAll(tasks);

        collector.TraceCount.Should().Be(100, "all 100 traces should be captured");
    }

    [Fact]
    public async Task ConcurrentCapture_SameTrace_AllSpansPresent()
    {
        var collector = new InMemoryTraceCollector();
        var tasks = Enumerable.Range(0, 50).Select(i =>
            Task.Run(() => collector.CaptureSpan(MakeSpan("shared-trace", $"op-{i}")))
        ).ToArray();

        await Task.WhenAll(tasks);

        collector.TraceCount.Should().Be(1);
        collector.GetSpans("shared-trace")!.Should().HaveCount(50);
    }

    #endregion

    #region Parent-Child Relationships

    [Fact]
    public void ParentChild_CorrectRelationships()
    {
        var collector = new InMemoryTraceCollector();
        var parentSpan = MakeSpan("trace-1", "agent.routing");
        var childSpan = MakeSpan("trace-1", "tool.call", parentSpanId: parentSpan.SpanId);
        var grandchildSpan = MakeSpan("trace-1", "tool.result", parentSpanId: childSpan.SpanId);

        collector.CaptureSpan(parentSpan);
        collector.CaptureSpan(childSpan);
        collector.CaptureSpan(grandchildSpan);

        var spans = collector.GetSpans("trace-1")!;

        var root = spans.First(s => s.ParentSpanId == null);
        root.OperationName.Should().Be("agent.routing");

        var child = spans.First(s => s.ParentSpanId == root.SpanId);
        child.OperationName.Should().Be("tool.call");

        var grandchild = spans.First(s => s.ParentSpanId == child.SpanId);
        grandchild.OperationName.Should().Be("tool.result");
    }

    #endregion
}

using FluentAssertions;
using RetailPulse.Api.Tracing;
using RetailPulse.Contracts.Tracing;

namespace RetailPulse.Tests.Tracing;

/// <summary>
/// Tests for TraceSummary generation.
/// Covers: step ordering, duration calculation, token aggregation,
///         cost estimation, unknown traceId.
/// 8+ tests.
/// </summary>
public class TraceSummaryTests
{
    private static TraceSpan MakeSpan(
        string traceId,
        string operationName,
        double durationMs = 100,
        int inputTokens = 0,
        int outputTokens = 0,
        decimal cost = 0m,
        DateTimeOffset? startTime = null)
    {
        var start = startTime ?? DateTimeOffset.UtcNow;
        return new TraceSpan(
            SpanId: Guid.NewGuid().ToString("N"),
            TraceId: traceId,
            ParentSpanId: null,
            OperationName: operationName,
            StartTime: start,
            EndTime: start.AddMilliseconds(durationMs),
            DurationMs: durationMs,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            EstimatedCostUsd: cost
        );
    }

    [Fact]
    public void GetSummary_IncludesAllStepsInOrder()
    {
        var collector = new InMemoryTraceCollector();
        var t0 = DateTimeOffset.UtcNow;
        collector.CaptureSpan(MakeSpan("trace-1", "step-3", startTime: t0.AddMilliseconds(200)));
        collector.CaptureSpan(MakeSpan("trace-1", "step-1", startTime: t0));
        collector.CaptureSpan(MakeSpan("trace-1", "step-2", startTime: t0.AddMilliseconds(100)));

        var summary = collector.GetSummary("trace-1")!;

        summary.Spans.Should().HaveCount(3);
        summary.Spans[0].OperationName.Should().Be("step-1");
        summary.Spans[1].OperationName.Should().Be("step-2");
        summary.Spans[2].OperationName.Should().Be("step-3");
    }

    [Fact]
    public void GetSummary_DurationCalculatedCorrectly()
    {
        var collector = new InMemoryTraceCollector();
        var t0 = DateTimeOffset.UtcNow;
        collector.CaptureSpan(MakeSpan("trace-1", "step-1", durationMs: 50, startTime: t0));
        collector.CaptureSpan(MakeSpan("trace-1", "step-2", durationMs: 100, startTime: t0.AddMilliseconds(50)));

        var summary = collector.GetSummary("trace-1")!;

        // Total duration = end of last span - start of first span
        summary.TotalDurationMs.Should().BeApproximately(150, 1);
    }

    [Fact]
    public void GetSummary_PerStepDuration_Correct()
    {
        var collector = new InMemoryTraceCollector();
        var t0 = DateTimeOffset.UtcNow;
        collector.CaptureSpan(MakeSpan("trace-1", "routing", durationMs: 25, startTime: t0));
        collector.CaptureSpan(MakeSpan("trace-1", "tool_call", durationMs: 200, startTime: t0.AddMilliseconds(25)));
        collector.CaptureSpan(MakeSpan("trace-1", "response", durationMs: 50, startTime: t0.AddMilliseconds(225)));

        var summary = collector.GetSummary("trace-1")!;

        summary.Spans[0].DurationMs.Should().Be(25);
        summary.Spans[1].DurationMs.Should().Be(200);
        summary.Spans[2].DurationMs.Should().Be(50);
    }

    [Fact]
    public void GetSummary_TokenCountsAggregated()
    {
        var collector = new InMemoryTraceCollector();
        collector.CaptureSpan(MakeSpan("trace-1", "routing", inputTokens: 100, outputTokens: 50));
        collector.CaptureSpan(MakeSpan("trace-1", "tool_call", inputTokens: 200, outputTokens: 100));
        collector.CaptureSpan(MakeSpan("trace-1", "response", inputTokens: 150, outputTokens: 300));

        var summary = collector.GetSummary("trace-1")!;

        summary.TotalInputTokens.Should().Be(450);
        summary.TotalOutputTokens.Should().Be(450);
    }

    [Fact]
    public void GetSummary_CostEstimation_UsesCorrectPricing()
    {
        var collector = new InMemoryTraceCollector();
        collector.CaptureSpan(MakeSpan("trace-1", "step-1", cost: 0.002m));
        collector.CaptureSpan(MakeSpan("trace-1", "step-2", cost: 0.005m));
        collector.CaptureSpan(MakeSpan("trace-1", "step-3", cost: 0.001m));

        var summary = collector.GetSummary("trace-1")!;

        summary.TotalEstimatedCostUsd.Should().Be(0.008m);
    }

    [Fact]
    public void GetSummary_UnknownTraceId_ReturnsNull()
    {
        var collector = new InMemoryTraceCollector();

        var summary = collector.GetSummary("nonexistent");

        summary.Should().BeNull();
    }

    [Fact]
    public void GetSummary_StartAndEndTimesCorrect()
    {
        var collector = new InMemoryTraceCollector();
        var t0 = DateTimeOffset.UtcNow;
        collector.CaptureSpan(MakeSpan("trace-1", "first", durationMs: 50, startTime: t0));
        collector.CaptureSpan(MakeSpan("trace-1", "last", durationMs: 100, startTime: t0.AddMilliseconds(200)));

        var summary = collector.GetSummary("trace-1")!;

        summary.StartTime.Should().Be(t0);
        summary.EndTime.Should().Be(t0.AddMilliseconds(300)); // 200 + 100 duration
    }

    [Fact]
    public void GetSummary_SingleSpan_CorrectSummary()
    {
        var collector = new InMemoryTraceCollector();
        var t0 = DateTimeOffset.UtcNow;
        collector.CaptureSpan(MakeSpan("trace-1", "only-step",
            durationMs: 42, inputTokens: 10, outputTokens: 20, cost: 0.001m, startTime: t0));

        var summary = collector.GetSummary("trace-1")!;

        summary.TraceId.Should().Be("trace-1");
        summary.Spans.Should().ContainSingle();
        summary.TotalDurationMs.Should().Be(42);
        summary.TotalInputTokens.Should().Be(10);
        summary.TotalOutputTokens.Should().Be(20);
        summary.TotalEstimatedCostUsd.Should().Be(0.001m);
    }

    [Fact]
    public void GetSummary_ZeroTokenSpans_AggregateToZero()
    {
        var collector = new InMemoryTraceCollector();
        collector.CaptureSpan(MakeSpan("trace-1", "step-1"));
        collector.CaptureSpan(MakeSpan("trace-1", "step-2"));

        var summary = collector.GetSummary("trace-1")!;

        summary.TotalInputTokens.Should().Be(0);
        summary.TotalOutputTokens.Should().Be(0);
        summary.TotalEstimatedCostUsd.Should().Be(0m);
    }
}

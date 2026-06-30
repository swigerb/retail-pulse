using FluentAssertions;
using RetailPulse.Api.Endpoints;
using RetailPulse.Api.Tracing;
using RetailPulse.Contracts.Tracing;

namespace RetailPulse.Tests.Observability;

public class ToolUsageStatsTests
{
    [Fact]
    public void GetToolStats_GroupsCountsTokensAndAveragesDuration()
    {
        var collector = new InMemoryTraceCollector();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        collector.CaptureSpan(MakeSpan("trace-1", "tool.GetInventory", now, durationMs: 100, inputTokens: 10, outputTokens: 5, toolName: "GetInventory"));
        collector.CaptureSpan(MakeSpan("trace-2", "tool.GetInventory", now.AddSeconds(1), durationMs: 300, inputTokens: 20, outputTokens: 15, toolName: "GetInventory"));
        collector.CaptureSpan(MakeSpan("trace-3", "agent.worker.process", now, durationMs: 500, inputTokens: 100, outputTokens: 100));

        IReadOnlyList<ToolUsageStat> stats = collector.GetToolStats(now.AddMinutes(-1));

        stats.Should().ContainSingle();
        stats[0].ToolName.Should().Be("GetInventory");
        stats[0].CallCount.Should().Be(2);
        stats[0].TotalTokens.Should().Be(50);
        stats[0].AvgDurationMs.Should().Be(200);
    }

    [Fact]
    public void GetToolStats_UsesSpanTypeTagForToolSpans()
    {
        var collector = new InMemoryTraceCollector();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        collector.CaptureSpan(MakeSpan(
            "trace-1",
            "custom.tool.operation",
            now,
            durationMs: 75,
            tags: new Dictionary<string, string>
            {
                ["span.type"] = "tool",
                ["tool.name"] = "LookupInventory"
            }));

        IReadOnlyList<ToolUsageStat> stats = collector.GetToolStats(now.AddMinutes(-1));

        stats.Should().ContainSingle();
        stats[0].ToolName.Should().Be("LookupInventory");
        stats[0].CallCount.Should().Be(1);
        stats[0].TotalTokens.Should().Be(0);
        stats[0].AvgDurationMs.Should().Be(75);
    }

    [Fact]
    public void GetToolStats_OrdersByCallCountThenTokensAndHonorsTop()
    {
        var collector = new InMemoryTraceCollector();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        collector.CaptureSpan(MakeSpan("trace-a", "tool.A", now, inputTokens: 500, outputTokens: 500));
        collector.CaptureSpan(MakeSpan("trace-b1", "tool.B", now));
        collector.CaptureSpan(MakeSpan("trace-b2", "tool.B", now));
        collector.CaptureSpan(MakeSpan("trace-c1", "tool.C", now, inputTokens: 300));
        collector.CaptureSpan(MakeSpan("trace-c2", "tool.C", now, outputTokens: 300));

        IReadOnlyList<ToolUsageStat> stats = collector.GetToolStats(now.AddMinutes(-1), top: 2);

        stats.Should().HaveCount(2);
        stats[0].ToolName.Should().Be("C");
        stats[0].CallCount.Should().Be(2);
        stats[0].TotalTokens.Should().Be(600);
        stats[1].ToolName.Should().Be("B");
    }

    [Fact]
    public void GetToolStats_FiltersBySinceCutoff()
    {
        var collector = new InMemoryTraceCollector();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        collector.CaptureSpan(MakeSpan("old-trace", "tool.OldTool", now.AddDays(-8)));
        collector.CaptureSpan(MakeSpan("new-trace", "tool.NewTool", now.AddDays(-1)));

        IReadOnlyList<ToolUsageStat> stats = collector.GetToolStats(now.AddDays(-7));

        stats.Should().ContainSingle();
        stats[0].ToolName.Should().Be("NewTool");
    }

    [Fact]
    public void GetToolStats_EmptyCollectorReturnsEmptyList()
    {
        var collector = new InMemoryTraceCollector();

        IReadOnlyList<ToolUsageStat> stats = collector.GetToolStats(DateTimeOffset.MinValue);

        stats.Should().BeEmpty();
    }

    [Fact]
    public void GetCostPeriodCutoff_UsesCostEndpointPeriodSemantics()
    {
        DateTimeOffset now = new(2026, 6, 30, 17, 11, 0, TimeSpan.Zero);

        ObservabilityEndpoints.GetCostPeriodCutoff("today", now).Should().Be(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
        ObservabilityEndpoints.GetCostPeriodCutoff("week", now).Should().Be(now.AddDays(-7));
        ObservabilityEndpoints.GetCostPeriodCutoff("month", now).Should().Be(now.AddDays(-30));
        ObservabilityEndpoints.GetCostPeriodCutoff("all", now).Should().Be(DateTimeOffset.MinValue);
        ObservabilityEndpoints.GetCostPeriodCutoff("bad-value", now).Should().Be(now.AddDays(-7));
    }

    private static TraceSpan MakeSpan(
        string traceId,
        string operationName,
        DateTimeOffset startTime,
        double durationMs = 100,
        int inputTokens = 0,
        int outputTokens = 0,
        string? toolName = null,
        IDictionary<string, string>? tags = null)
    {
        Dictionary<string, string>? spanTags = tags is null ? null : new Dictionary<string, string>(tags);
        if (toolName is not null)
        {
            spanTags ??= [];
            spanTags["tool.name"] = toolName;
        }

        return new TraceSpan(
            SpanId: Guid.NewGuid().ToString("N"),
            TraceId: traceId,
            ParentSpanId: null,
            OperationName: operationName,
            StartTime: startTime,
            EndTime: startTime.AddMilliseconds(durationMs),
            DurationMs: durationMs,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            Tags: spanTags);
    }
}

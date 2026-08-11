using FluentAssertions;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Budget;
using RetailPulse.Contracts;
using Xunit;

namespace RetailPulse.Tests.Agents;

/// <summary>
/// Publix production sweep #76 telemetry gap — the documented 25K
/// tool-context budget is measured in-API by <see cref="RequestToolContext"/>
/// but is not exposed on <c>/api/chat</c>, forcing acceptance to use an
/// over-counting wire proxy. The response now carries a
/// <see cref="ToolContextTelemetry"/> block with the real CumulativeChars,
/// DistinctCalls, and effective caps so acceptance tooling can gate
/// directly. This suite pins the surface contract.
/// </summary>
public sealed class ToolContextTelemetrySurfaceTests
{
    [Fact]
    public void ChatResponse_HasNullableToolContextField()
    {
        // Pin the DTO shape — a ToolContextTelemetry property must exist on
        // ChatResponse and be nullable so tests/legacy callers that don't run
        // through the pipeline are not forced to construct it.
        var response = new ChatResponse(
            Reply: "hi",
            SessionId: "s",
            Spans: [],
            ToolContext: new ToolContextTelemetry(
                CumulativeChars: 12345,
                DistinctCalls: 3,
                MaxCumulativeChars: 25_000,
                MaxToolCalls: 5,
                IsChartIntent: true));

        response.ToolContext.Should().NotBeNull();
        response.ToolContext.CumulativeChars.Should().Be(12345);
        response.ToolContext.DistinctCalls.Should().Be(3);
        response.ToolContext.MaxCumulativeChars.Should().Be(25_000);
        response.ToolContext.MaxToolCalls.Should().Be(5);
        response.ToolContext.IsChartIntent.Should().BeTrue();
    }

    [Fact]
    public void BuildToolContextTelemetry_ReflectsLiveRequestToolContext()
    {
        using IDisposable scope = RequestToolContext.Begin("test-principal", isChartIntent: true);
        RequestToolContext? ctx = RequestToolContext.Current;
        ctx.Should().NotBeNull();

        ctx.Record(
            key: ctx.BuildKey("SomeTool", "{}"),
            json: new string('x', 4321),
            metrics: new ToolResultMetrics
            {
                ToolName = "SomeTool",
                OriginalChars = 4321,
                ReturnedChars = 4321,
                EstimatedTokens = 1080,
            });

        ToolContextTelemetry? telemetry = AgentExecutionPipeline.BuildToolContextTelemetry(thoughtActivity: null);

        telemetry.Should().NotBeNull();
        telemetry.CumulativeChars.Should().Be(4321);
        telemetry.DistinctCalls.Should().Be(1);
        telemetry.IsChartIntent.Should().BeTrue();
        // The chart-intent cap must apply and be at most the global cap.
        telemetry.MaxToolCalls.Should().BeLessThanOrEqualTo(new ToolResultBudgetOptions().MaxToolCalls);
    }

    [Fact]
    public void BuildToolContextTelemetry_ReturnsNull_WhenNoScopeActive()
    {
        // Ensure no scope leaks from a previous test.
        AgentExecutionPipeline.BuildToolContextTelemetry(thoughtActivity: null)
            .Should().BeNull();
    }
}

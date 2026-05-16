using System.Diagnostics;
using FluentAssertions;
using RetailPulse.Api.Middleware;

namespace RetailPulse.Tests;

public class AgentTelemetryTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _activities = [];

    public AgentTelemetryTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "RetailPulse.Agent",
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => _activities.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void StartAgentThought_CreatesActivityWithCorrectTags()
    {
        using Activity? activity = AgentTelemetry.StartAgentThought("test-agent", "What is the status?");

        activity.Should().NotBeNull();
        activity.OperationName.Should().Be("agent.thought");
        activity.GetTagItem("agent.name").Should().Be("test-agent");
        activity.GetTagItem("agent.prompt_length").Should().Be("What is the status?".Length);
    }

    [Fact]
    public void StartToolCall_CreatesActivityWithCorrectTags()
    {
        using Activity? activity = AgentTelemetry.StartToolCall("GetDepletionStats", /*lang=json,strict*/ "{\"brand\":\"Patron\"}");

        activity.Should().NotBeNull();
        activity.OperationName.Should().Be("tool.GetDepletionStats");
        activity.Kind.Should().Be(ActivityKind.Client);
        activity.GetTagItem("tool.name").Should().Be("GetDepletionStats");
        activity.GetTagItem("tool.arguments").Should().Be(/*lang=json,strict*/ "{\"brand\":\"Patron\"}");
    }

    [Fact]
    public void StartToolResult_CreatesActivityWithCorrectTags()
    {
        using Activity? activity = AgentTelemetry.StartToolResult("GetDepletionStats", 256);

        activity.Should().NotBeNull();
        activity.OperationName.Should().Be("tool.GetDepletionStats.result");
        activity.GetTagItem("tool.name").Should().Be("GetDepletionStats");
        activity.GetTagItem("tool.result_length").Should().Be(256);
    }

    [Fact]
    public void StartAgentResponse_CreatesActivityWithCorrectTags()
    {
        using Activity? activity = AgentTelemetry.StartAgentResponse("retail-pulse");

        activity.Should().NotBeNull();
        activity.OperationName.Should().Be("agent.response");
        activity.GetTagItem("agent.name").Should().Be("retail-pulse");
    }
}

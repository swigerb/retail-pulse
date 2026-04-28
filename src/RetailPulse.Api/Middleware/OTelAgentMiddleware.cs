using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Models;

namespace RetailPulse.Api.Middleware;

public static class AgentTelemetry
{
    public static readonly ActivitySource Source = new("RetailPulse.Agent");

    public static Activity? StartAgentThought(string agentName, string prompt)
    {
        var activity = Source.StartActivity("agent.thought", ActivityKind.Internal);
        activity?.SetTag("agent.name", agentName);
        activity?.SetTag("agent.prompt_length", prompt.Length);
        return activity;
    }

    public static Activity? StartToolCall(string toolName, string arguments)
    {
        var activity = Source.StartActivity($"tool.{toolName}", ActivityKind.Client);
        activity?.SetTag("tool.name", toolName);
        activity?.SetTag("tool.arguments", arguments);
        return activity;
    }

    public static Activity? StartToolResult(string toolName, int resultLength)
    {
        var activity = Source.StartActivity($"tool.{toolName}.result", ActivityKind.Internal);
        activity?.SetTag("tool.name", toolName);
        activity?.SetTag("tool.result_length", resultLength);
        return activity;
    }

    public static Activity? StartAgentResponse(string agentName)
    {
        var activity = Source.StartActivity("agent.response", ActivityKind.Internal);
        activity?.SetTag("agent.name", agentName);
        return activity;
    }
}

public class TelemetryCollector
{
    private readonly IHubContext<TelemetryHub> _hubContext;
    private readonly List<AgentSpan> _spans = new();

    public TelemetryCollector(IHubContext<TelemetryHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public IReadOnlyList<AgentSpan> Spans => _spans;

    public async Task RecordSpanAsync(string name, string type, string detail, double durationMs)
    {
        var span = new AgentSpan(name, type, detail, durationMs, DateTimeOffset.UtcNow);
        _spans.Add(span);

        // Push to connected clients in real-time
        await _hubContext.Clients.All.SendAsync("SpanReceived", span);
    }
}

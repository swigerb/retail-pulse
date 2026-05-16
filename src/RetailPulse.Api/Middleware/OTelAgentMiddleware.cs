using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using RetailPulse.Api.Hubs;
using RetailPulse.Contracts;

namespace RetailPulse.Api.Middleware;

public static class AgentTelemetry
{
    public static readonly ActivitySource Source = new("RetailPulse.Agent");

    // ── Root span: chat_request ─────────────────────────────────────────

    public static Activity? StartChatRequest(string sessionId, string message)
    {
        Activity? activity = Source.StartActivity("chat_request", ActivityKind.Server);
        activity?.SetTag("session.id", sessionId);
        activity?.SetTag("message.length", message.Length);
        return activity;
    }

    // ── Router spans ────────────────────────────────────────────────────

    public static Activity? StartRouterClassify(string message)
    {
        Activity? activity = Source.StartActivity("router.classify", ActivityKind.Internal);
        activity?.SetTag("message.length", message.Length);
        return activity;
    }

    public static Activity? StartRouterSelectAgent()
    {
        Activity? activity = Source.StartActivity("router.select_agent", ActivityKind.Internal);
        return activity;
    }

    // ── Agent spans ─────────────────────────────────────────────────────

    public static Activity? StartAgentProcess(string agentName)
    {
        Activity? activity = Source.StartActivity($"agent.{agentName}.process", ActivityKind.Internal);
        activity?.SetTag("agent.name", agentName);
        return activity;
    }

    public static Activity? StartAgentThought(string agentName, string prompt)
    {
        Activity? activity = Source.StartActivity("agent.thought", ActivityKind.Internal);
        activity?.SetTag("agent.name", agentName);
        activity?.SetTag("agent.prompt_length", prompt.Length);
        return activity;
    }

    // ── Tool spans ──────────────────────────────────────────────────────

    public static Activity? StartToolCall(string toolName, string arguments)
    {
        Activity? activity = Source.StartActivity($"tool.{toolName}", ActivityKind.Client);
        activity?.SetTag("tool.name", toolName);
        activity?.SetTag("tool.arguments", arguments);
        return activity;
    }

    public static Activity? StartToolResult(string toolName, int resultLength)
    {
        Activity? activity = Source.StartActivity($"tool.{toolName}.result", ActivityKind.Internal);
        activity?.SetTag("tool.name", toolName);
        activity?.SetTag("tool.result_length", resultLength);
        return activity;
    }

    // ── Memory spans ────────────────────────────────────────────────────

    public static Activity? StartMemoryRecall(string userId)
    {
        Activity? activity = Source.StartActivity("memory.recall", ActivityKind.Internal);
        activity?.SetTag("memory.user_id", userId);
        return activity;
    }

    public static Activity? StartMemoryStore(string userId)
    {
        Activity? activity = Source.StartActivity("memory.store", ActivityKind.Internal);
        activity?.SetTag("memory.user_id", userId);
        return activity;
    }

    // ── Approval spans ──────────────────────────────────────────────────

    public static Activity? StartApprovalRequest(string agentId, string action)
    {
        Activity? activity = Source.StartActivity("approval.request", ActivityKind.Internal);
        activity?.SetTag("approval.agent_id", agentId);
        activity?.SetTag("approval.action", action);
        return activity;
    }

    public static Activity? StartApprovalWait(string requestId)
    {
        Activity? activity = Source.StartActivity("approval.wait", ActivityKind.Internal);
        activity?.SetTag("approval.request_id", requestId);
        return activity;
    }

    // ── Response span ───────────────────────────────────────────────────

    public static Activity? StartAgentResponse(string agentName)
    {
        Activity? activity = Source.StartActivity("agent.response", ActivityKind.Internal);
        activity?.SetTag("agent.name", agentName);
        return activity;
    }
}

/// <summary>
/// Collects spans for a single chat session and pushes them to SignalR
/// clients that have joined the matching session group. Spans are NOT
/// broadcast to all clients (security: telemetry can contain prompt text
/// and tool arguments).
/// </summary>
public class TelemetryCollector
{
    private readonly IHubContext<TelemetryHub> _hubContext;
    private readonly string? _sessionId;
    private readonly ConcurrentQueue<AgentSpan> _spans = new();

    public TelemetryCollector(IHubContext<TelemetryHub> hubContext, string? sessionId = null)
    {
        _hubContext = hubContext;
        _sessionId = sessionId;
    }

    public IReadOnlyCollection<AgentSpan> Spans => _spans;

    public async Task RecordSpanAsync(string name, string type, string detail, double durationMs, int? inputTokens = null, int? outputTokens = null)
    {
        var span = new AgentSpan(name, type, detail, durationMs, DateTimeOffset.UtcNow, _sessionId, inputTokens, outputTokens);
        _spans.Enqueue(span);

        // Send telemetry to the session group if available, otherwise broadcast to all
        if (!string.IsNullOrEmpty(_sessionId))
        {
            await _hubContext.Clients.Group(_sessionId).SendAsync("SpanReceived", span);
        }
        else
        {
            await _hubContext.Clients.All.SendAsync("SpanReceived", span);
        }
    }
}

using RetailPulse.Contracts;

namespace RetailPulse.Api.Models;

public record ChatRequest(string Message, string? SessionId = null);

public record ChatResponse(
    string Reply,
    string SessionId,
    List<AgentSpan> Spans,
    List<ChartSpec>? Charts = null
);

public record AgentSpan(
    string Name,
    string Type, // "thought", "tool_call", "tool_result", "response"
    string Detail,
    double DurationMs,
    DateTimeOffset Timestamp
);

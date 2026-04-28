using RetailPulse.Contracts;

namespace RetailPulse.TeamsBot.Models;

/// <summary>
/// Request model for RetailPulse API chat endpoint
/// </summary>
public record ChatRequest(
    string Message, 
    string? SessionId = null,
    UserContext? User = null
);

/// <summary>
/// User context information for personalized responses
/// </summary>
public record UserContext(
    string ObjectId,
    string DisplayName,
    string Email
);

/// <summary>
/// Response model from RetailPulse API chat endpoint
/// </summary>
public record ChatResponse(
    string Reply,
    string SessionId,
    List<AgentSpan> Spans,
    List<ChartSpec>? Charts = null
);

/// <summary>
/// Telemetry span from the agent pipeline
/// </summary>
public record AgentSpan(
    string Name,
    string Type, // "thought", "tool_call", "tool_result", "response"
    string Detail,
    double DurationMs,
    DateTimeOffset Timestamp
);


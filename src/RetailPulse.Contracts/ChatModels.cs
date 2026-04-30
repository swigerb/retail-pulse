namespace RetailPulse.Contracts;

/// <summary>
/// Request model for the RetailPulse chat endpoint.
/// Shared between the Api and TeamsBot projects (P1 — eliminates duplicate DTOs).
/// </summary>
public record ChatRequest(
    string Message,
    string? SessionId = null,
    UserContext? User = null
);

/// <summary>
/// Response model for the RetailPulse chat endpoint.
/// </summary>
public record ChatResponse(
    string Reply,
    string SessionId,
    List<AgentSpan> Spans,
    List<ChartSpec>? Charts = null
);

/// <summary>
/// Telemetry span emitted by the agent pipeline. SessionId is included so
/// SignalR clients can route spans to the right session.
/// </summary>
public record AgentSpan(
    string Name,
    string Type, // "thought", "tool_call", "tool_result", "response"
    string Detail,
    double DurationMs,
    DateTimeOffset Timestamp,
    string? SessionId = null
);

/// <summary>
/// Authenticated user context flowed from Teams SSO to the API.
/// </summary>
public record UserContext(
    string ObjectId,
    string DisplayName,
    string Email
);

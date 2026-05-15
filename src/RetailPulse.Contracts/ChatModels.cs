namespace RetailPulse.Contracts;

/// <summary>
/// Prior conversation message included with a chat request.
/// </summary>
public record ChatHistoryMessage(string Role, string Content);

/// <summary>
/// Request model for the RetailPulse chat endpoint.
/// Shared between the Api and TeamsBot projects (P1 — eliminates duplicate DTOs).
/// </summary>
public record ChatRequest(
    string Message,
    string? SessionId = null,
    UserContext? User = null,
    List<ChatHistoryMessage>? History = null
);

/// <summary>
/// Routing metadata included in the chat response for telemetry display.
/// </summary>
public record RoutingInfo(
    string AgentKey,
    string AgentName,
    string? Intent,
    double? Confidence,
    long? DurationMs
);

/// <summary>
/// Response model for the RetailPulse chat endpoint.
/// </summary>
public record ChatResponse(
    string Reply,
    string SessionId,
    List<AgentSpan> Spans,
    List<ChartSpec>? Charts = null,
    long? TotalDurationMs = null,
    TokenUsage? TokenUsage = null,
    RoutingInfo? Routing = null
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
    string? SessionId = null,
    int? InputTokens = null,
    int? OutputTokens = null
);

/// <summary>
/// Aggregated token usage for a single chat turn, including estimated cost.
/// </summary>
public record TokenUsage(
    int InputTokens,
    int OutputTokens,
    int TotalTokens,
    decimal? EstimatedCostUsd = null
);

/// <summary>
/// Authenticated user context flowed from Teams SSO to the API.
/// </summary>
public record UserContext(
    string ObjectId,
    string DisplayName,
    string Email
);

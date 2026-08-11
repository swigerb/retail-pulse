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
    RoutingInfo? Routing = null,
    ToolContextTelemetry? ToolContext = null
);

/// <summary>
/// Per-request tool-context accounting exposed on the chat response so acceptance
/// tooling can gate on the real in-API measurement rather than an over-counting wire
/// proxy. Mirrors the counters kept by <c>RequestToolContext</c>:
/// cumulative returned-JSON characters actually placed into model context, the number
/// of distinct (non-deduplicated) tool invocations, and the caps in effect for this
/// request. Never contains payload content — sizes and flags only.
/// </summary>
/// <param name="CumulativeChars">Total characters of tool results that entered model context this request.</param>
/// <param name="DistinctCalls">Distinct (non-deduplicated) tool invocations this request.</param>
/// <param name="MaxCumulativeChars">Configured cumulative-character budget for this request.</param>
/// <param name="MaxToolCalls">Configured distinct-call cap for this request (chart-intent cap applied when tighter).</param>
/// <param name="IsChartIntent">True when the request was classified as an explicit chart-request intent.</param>
public record ToolContextTelemetry(
    int CumulativeChars,
    int DistinctCalls,
    int MaxCumulativeChars,
    int MaxToolCalls,
    bool IsChartIntent
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

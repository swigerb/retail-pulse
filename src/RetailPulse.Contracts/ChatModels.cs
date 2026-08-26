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
    List<ChatHistoryMessage>? History = null,
    string? ForceExecutionPath = null,
    /// <summary>
    /// The user's original, unmodified request, when <see cref="Message"/> is a derived
    /// or scoped rewrite of it — as the plan path does, weaving a step action into the
    /// message it hands each specialist.
    ///
    /// Chart intent MUST be detected from this rather than from the rewritten message:
    /// a step action such as "chart the regional rollup as a bar" would otherwise win
    /// over the user's actual "create a table …" ask, because the detector matches the
    /// first chart-type phrase it sees. Null on the single-shot path, where
    /// <see cref="Message"/> already is the original.
    /// </summary>
    string? OriginalMessage = null
)
{
    /// <summary>
    /// The text that represents what the USER asked for — <see cref="OriginalMessage"/>
    /// when a scoped rewrite is in play, otherwise <see cref="Message"/>.
    /// </summary>
    public string UserIntentMessage =>
        string.IsNullOrWhiteSpace(OriginalMessage) ? Message : OriginalMessage;
}

/// <summary>
/// Routing metadata included in the chat response for telemetry display.
/// </summary>
public record RoutingInfo(
    string AgentKey,
    string AgentName,
    string? Intent,
    double? Confidence,
    long? DurationMs,
    string? ExecutionPath = null,
    bool? ExecutionPathForced = null
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
    /// <summary>
    /// Populated on the plan-first branch (#93/#96) so the client can drive the
    /// plan surface (steps, review, history) without a follow-up lookup.
    /// Always null on the fast/single-shot path so no plan chrome leaks into
    /// those replies.
    /// </summary>
    string? PlanId = null
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

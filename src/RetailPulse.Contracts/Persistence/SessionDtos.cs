namespace RetailPulse.Contracts.Persistence;

/// <summary>
/// Summary view of a persisted chat session — one row per session in the caller's list endpoint.
/// Content is intentionally omitted; use <see cref="SessionDetailDto"/> to rehydrate turns.
/// </summary>
public record SessionSummaryDto(
    string SessionId,
    string? TenantId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    int TurnCount);

/// <summary>
/// Full session detail, including the ordered turn history used to rehydrate the browser.
/// The store returns the caller's own sessions only; a request for a session owned by a
/// different subject fails with a 404 rather than a silent empty success.
/// </summary>
public record SessionDetailDto(
    string SessionId,
    string? TenantId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    IReadOnlyList<SessionTurnDto> Turns);

/// <summary>
/// One persisted chat turn — either a user prompt or an assistant reply. The role names
/// match the values the chat pipeline already emits into <see cref="ChatHistoryMessage"/>
/// so a rehydrated session can be posted straight back into <c>/api/chat</c>.
/// </summary>
public record SessionTurnDto(
    string TurnId,
    string Role,
    string Content,
    string? AgentId,
    string? RoutingIntent,
    string? RoutingAgentKey,
    double? RoutingConfidence,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    IReadOnlyList<ChartSpec>? Charts,
    string? SpanSummary,
    DateTimeOffset Timestamp);

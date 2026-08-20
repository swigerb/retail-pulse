using RetailPulse.Contracts;

namespace RetailPulse.Api.Persistence;

/// <summary>
/// Envelope handed to <see cref="ISessionStore.PersistTurnAsync"/> so the chat endpoint
/// can express a single write intent (session-upsert + turn-insert) without exposing
/// SQL details. The store treats this as an atomic pair — the session row is upserted
/// (id, subject, tenant, created, last activity) and the turn row is appended.
/// </summary>
public sealed record SessionTurnWrite
{
    public required string SessionId { get; init; }

    /// <summary>
    /// Resolved via <see cref="Auth.UserIdentity.Resolve"/>. Anonymous callers
    /// never reach the store; the caller is responsible for filtering them out per policy.
    /// </summary>
    public required string Subject { get; init; }

    /// <summary>
    /// Tenant identifier resolved from <see cref="ITenantProvider"/>.
    /// Kept nullable so it degrades gracefully when a tenant configuration is not loaded
    /// (the default constructor of <see cref="TenantConfiguration"/>
    /// leaves <see cref="TenantConfiguration.Company"/> blank).
    /// </summary>
    public string? TenantId { get; init; }

    public required string Role { get; init; }

    public required string Content { get; init; }

    public string? AgentId { get; init; }

    public string? RoutingIntent { get; init; }

    public string? RoutingAgentKey { get; init; }

    public double? RoutingConfidence { get; init; }

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    public int? TotalTokens { get; init; }

    /// <summary>Chart specs emitted by the assistant turn; <c>null</c> for user turns and no-chart replies.</summary>
    public IReadOnlyList<ChartSpec>? Charts { get; init; }

    /// <summary>
    /// Short, JSON-shaped summary of the pipeline spans (tool call names, counts, total
    /// duration). The full trace tree lives in <see cref="Tracing.InMemoryTraceCollector"/>;
    /// this is only enough to recover an at-a-glance view when a session is rehydrated
    /// long after the trace ring buffer has recycled.
    /// </summary>
    public string? SpanSummary { get; init; }

    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Result of a cleanup sweep — the number of sessions and turns evicted for observability.
/// </summary>
public readonly record struct CleanupResult(int SessionsDeleted, int TurnsDeleted);

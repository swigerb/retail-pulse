namespace RetailPulse.Contracts.Guardrails;

/// <summary>
/// Audit log for blocked or suspicious requests detected by the guardrails pipeline.
/// Implementations must be thread-safe.
/// </summary>
public interface ISuspiciousRequestLog
{
    /// <summary>
    /// Records a suspicious request event.
    /// </summary>
    Task LogAsync(SuspiciousRequest request, CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent suspicious request entries.
    /// </summary>
    Task<IReadOnlyList<SuspiciousRequest>> GetRecentAsync(int count = 50, CancellationToken ct = default);

    /// <summary>
    /// Returns aggregated guardrails statistics since the service started.
    /// </summary>
    Task<GuardrailsStats> GetStatsAsync(CancellationToken ct = default);
}

/// <summary>
/// A single suspicious request event captured by the guardrails pipeline.
/// Every field after <see cref="Action"/> is additive and defaults to
/// <c>null</c> so existing positional callers and fixtures keep compiling.
///
/// This record is the single source of truth for what an audit row MEANS.
/// The guardrails dashboard renders <see cref="Reason"/> and
/// <see cref="Subject"/> directly and must never reverse-engineer them by
/// pattern-matching <see cref="RequestText"/>: that prose is user-supplied or
/// diagnostic and its wording is not a contract. Every call site is therefore
/// required to populate <see cref="Stage"/>, <see cref="Reason"/>, and, where
/// the event concerns a named thing, <see cref="Subject"/>.
/// </summary>
public record SuspiciousRequest(
    string Id,
    DateTime Timestamp,
    string RequestText,
    string DetectionType,
    string UserContext,
    string Action,
    string? Category = null,
    int? Severity = null,
    string? Decision = null,
    string? Stage = null,
    int? Threshold = null,
    string? Reason = null,
    string? Subject = null);

/// <summary>
/// Aggregated guardrails activity metrics.
/// </summary>
public record GuardrailsStats(
    int TotalBlocked,
    int JailbreakAttempts,
    int PiiDetections,
    int AccessDenials,
    DateTime Since,
    int ContentSafetyBlocks = 0,
    int ContentSafetyFlags = 0);

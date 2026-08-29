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
/// The <see cref="Category"/>, <see cref="Severity"/>, and <see cref="Decision"/>
/// fields are additive and default to <c>null</c> — existing pattern-layer
/// callers keep their positional constructor and fixtures unchanged, and the
/// Content Safety layer populates them on every block path so severity is
/// never buried inside free-text.
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
    string? Reason = null);

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

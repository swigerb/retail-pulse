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
/// </summary>
public record SuspiciousRequest(
    string Id,
    DateTime Timestamp,
    string RequestText,
    string DetectionType,
    string UserContext,
    string Action);

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

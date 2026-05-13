namespace RetailPulse.Contracts.Alerts;

/// <summary>
/// Proactive alert subsystem — detects demand/supply anomalies
/// and provides snooze, dismiss, and history management.
/// </summary>
public interface IAlertService
{
    /// <summary>
    /// Run anomaly detection across all brand/region combinations.
    /// Returns any new alerts that fired (after throttle and snooze filtering).
    /// </summary>
    Task<IReadOnlyList<Alert>> CheckForAlertsAsync(CancellationToken ct = default);

    /// <summary>Suppress alerts matching a type (and optional brand/region) for a user.</summary>
    Task SnoozeAsync(string alertType, string userId, TimeSpan duration, CancellationToken ct = default);

    /// <summary>Mark an alert as seen/dismissed for a user.</summary>
    Task DismissAsync(string alertId, string userId, CancellationToken ct = default);

    /// <summary>Retrieve recent alert history, newest first.</summary>
    Task<IReadOnlyList<Alert>> GetHistoryAsync(string userId, int limit = 50, CancellationToken ct = default);

    /// <summary>Return currently active (unfired within recency window) alerts.</summary>
    Task<IReadOnlyList<Alert>> GetActiveAlertsAsync(CancellationToken ct = default);
}

/// <summary>
/// A detected anomaly in demand, supply, or sentiment data.
/// </summary>
public record Alert(
    string Id,
    string Type,              // "demand_spike", "supply_drop", "trend_reversal"
    string Severity,          // "high", "medium", "low"
    string Title,
    string Description,
    string Brand,
    string Region,
    string RecommendedAction,
    DateTimeOffset DetectedAt,
    Dictionary<string, object>? Metadata = null  // extra context (% change, affected SKUs, etc.)
);

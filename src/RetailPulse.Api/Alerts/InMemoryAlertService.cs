using System.Collections.Concurrent;
using RetailPulse.Contracts.Alerts;

namespace RetailPulse.Api.Alerts;

/// <summary>
/// In-memory implementation of IAlertService for testing and development.
/// Implements throttling, snooze, and dismiss logic.
/// 
/// Anomaly rules:
///   - Demand spike:   volume >20% above baseline
///   - Supply drop:    volume >15% below baseline
///   - Trend reversal: direction change >10%
/// 
/// Severity classification:
///   - >40% = high
///   - >20% = medium
///   - otherwise low
/// 
/// Throttling: max 1 alert per (type, brand, region) per hour
/// </summary>
public class InMemoryAlertService : IAlertService
{
    private readonly ConcurrentBag<Alert> _alerts = [];
    private readonly ConcurrentDictionary<string, DateTimeOffset> _throttleMap = new();
    private readonly ConcurrentBag<SnoozeEntry> _snoozes = [];
    private readonly ConcurrentBag<DismissEntry> _dismissals = [];
    private readonly List<AnomalyDataPoint> _dataPoints = [];
    private readonly TimeSpan _throttleWindow;

    public InMemoryAlertService(TimeSpan? throttleWindow = null)
    {
        _throttleWindow = throttleWindow ?? TimeSpan.FromHours(1);
    }

    /// <summary>
    /// Seeds anomaly data points for testing.
    /// </summary>
    public void SeedDataPoint(string brand, string region, string type, double baseline, double current) => _dataPoints.Add(new AnomalyDataPoint(brand, region, type, baseline, current));

    public Task<IReadOnlyList<Alert>> CheckForAlertsAsync(CancellationToken ct = default)
    {
        var newAlerts = new List<Alert>();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (AnomalyDataPoint dp in _dataPoints)
        {
            double deviation = dp.Type switch
            {
                "demand_spike" => (dp.Current - dp.Baseline) / dp.Baseline * 100,
                "supply_drop" => (dp.Baseline - dp.Current) / dp.Baseline * 100,
                "trend_reversal" => Math.Abs(dp.Current - dp.Baseline) / dp.Baseline * 100,
                _ => 0
            };

            double threshold = dp.Type switch
            {
                "demand_spike" => 20.0,
                "supply_drop" => 15.0,
                "trend_reversal" => 10.0,
                _ => double.MaxValue
            };

            if (deviation <= threshold)
                continue;

            // Throttle check
            string throttleKey = $"{dp.Type}|{dp.Brand}|{dp.Region}";
            if (_throttleMap.TryGetValue(throttleKey, out DateTimeOffset lastFired) &&
                now - lastFired < _throttleWindow)
            {
                continue;
            }

            string severity = ClassifySeverity(deviation);
            var alert = new Alert(
                Id: Guid.NewGuid().ToString("N"),
                Type: dp.Type,
                Severity: severity,
                Title: $"{FormatType(dp.Type)} detected for {dp.Brand} in {dp.Region}",
                Description: $"{deviation:F1}% deviation from baseline",
                Brand: dp.Brand,
                Region: dp.Region,
                RecommendedAction: $"Review {dp.Brand} {dp.Type} in {dp.Region}",
                DetectedAt: now,
                Metadata: new Dictionary<string, object>
                {
                    ["deviationPercent"] = deviation,
                    ["baseline"] = dp.Baseline,
                    ["current"] = dp.Current
                }
            );

            _throttleMap[throttleKey] = now;
            _alerts.Add(alert);
            newAlerts.Add(alert);
        }

        return Task.FromResult<IReadOnlyList<Alert>>(newAlerts);
    }

    public Task SnoozeAsync(string alertType, string userId, TimeSpan duration, CancellationToken ct = default) => SnoozeWithDetailsAsync(alertType, userId, duration, null, null, ct);

    /// <summary>
    /// Snooze with optional brand/region specificity.
    /// </summary>
    public Task SnoozeWithDetailsAsync(string alertType, string userId, TimeSpan duration,
        string? brand = null, string? region = null, CancellationToken ct = default)
    {
        _snoozes.Add(new SnoozeEntry(userId, alertType, brand, region,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow + duration));
        return Task.CompletedTask;
    }

    public Task DismissAsync(string alertId, string userId, CancellationToken ct = default)
    {
        _dismissals.Add(new DismissEntry(alertId, userId, DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Alert>> GetHistoryAsync(string userId, int limit = 50, CancellationToken ct = default)
    {
        var result = _alerts
            .OrderByDescending(a => a.DetectedAt)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<Alert>>(result);
    }

    public Task<IReadOnlyList<Alert>> GetActiveAlertsAsync(CancellationToken ct = default)
    {
        var dismissed = _dismissals.Select(d => d.AlertId).ToHashSet();
        var result = _alerts
            .Where(a => !dismissed.Contains(a.Id))
            .OrderByDescending(a => a.DetectedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<Alert>>(result);
    }

    /// <summary>
    /// Returns active alerts filtered by user's snooze and dismiss records.
    /// </summary>
    public Task<IReadOnlyList<Alert>> GetActiveForUserAsync(string userId, CancellationToken ct = default)
    {
        var dismissed = _dismissals.Where(d => d.UserId == userId).Select(d => d.AlertId).ToHashSet();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var activeSnoozesForUser = _snoozes
            .Where(s => s.UserId == userId && s.ExpiresAt > now)
            .ToList();

        var result = _alerts
            .Where(a => !dismissed.Contains(a.Id))
            .Where(a => !IsSnoozed(a, activeSnoozesForUser))
            .OrderByDescending(a => a.DetectedAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<Alert>>(result);
    }

    /// <summary>
    /// Checks whether a throttle entry exists for the given key and is still within the window.
    /// </summary>
    public bool IsThrottled(string type, string brand, string region)
    {
        string key = $"{type}|{brand}|{region}";
        return _throttleMap.TryGetValue(key, out DateTimeOffset lastFired) &&
               DateTimeOffset.UtcNow - lastFired < _throttleWindow;
    }

    /// <summary>
    /// Exposes throttle state for testing. Resets throttle for a specific key.
    /// </summary>
    public void ResetThrottle(string type, string brand, string region)
    {
        string key = $"{type}|{brand}|{region}";
        _throttleMap.TryRemove(key, out _);
    }

    /// <summary>
    /// Force-sets a throttle timestamp in the past for testing throttle expiry.
    /// </summary>
    public void SetThrottleTimestamp(string type, string brand, string region, DateTimeOffset timestamp)
    {
        string key = $"{type}|{brand}|{region}";
        _throttleMap[key] = timestamp;
    }

    public IReadOnlyList<SnoozeEntry> GetSnoozes(string userId) => [.. _snoozes.Where(s => s.UserId == userId)];

    public int AlertCount => _alerts.Count;

    private static string ClassifySeverity(double deviationPercent)
    {
        return deviationPercent switch
        {
            > 40 => "high",
            > 20 => "medium",
            _ => "low"
        };
    }

    private static string FormatType(string type) => type switch
    {
        "demand_spike" => "Demand Spike",
        "supply_drop" => "Supply Drop",
        "trend_reversal" => "Trend Reversal",
        _ => type
    };

    private static bool IsSnoozed(Alert alert, List<SnoozeEntry> snoozes)
    {
        return snoozes.Any(s =>
            (s.Type == null || s.Type == alert.Type) &&
            (s.Brand == null || s.Brand == alert.Brand) &&
            (s.Region == null || s.Region == alert.Region));
    }

    public record AnomalyDataPoint(string Brand, string Region, string Type, double Baseline, double Current);
    public record SnoozeEntry(string UserId, string? Type, string? Brand, string? Region, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
    public record DismissEntry(string AlertId, string UserId, DateTimeOffset DismissedAt);
}

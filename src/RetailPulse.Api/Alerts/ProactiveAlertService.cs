using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using RetailPulse.Api.Hubs;
using RetailPulse.Contracts.Alerts;

namespace RetailPulse.Api.Alerts;

/// <summary>
/// Background service that periodically scans demand data for anomalies
/// and pushes alerts to SignalR + SQLite. Configurable via appsettings.json.
///
/// Algorithm (simple, demo-grade):
///   current_period = last 7 days avg volume
///   baseline_period = 8-37 days ago avg volume
///   pct_change = (current - baseline) / baseline * 100
///   
///   demand_spike:   pct_change > 20  (high if > 40)
///   supply_drop:    pct_change < -15 (high if < -30)
///   trend_reversal: sign(7-day trend) != sign(30-day trend) AND abs(pct_change) > 10
/// </summary>
public sealed class ProactiveAlertService : BackgroundService
{
    private readonly SqliteAlertService _alertStore;
    private readonly IHubContext<TelemetryHub> _hubContext;
    private readonly HttpClient _mcpClient;
    private readonly ILogger<ProactiveAlertService> _logger;
    private readonly TimeSpan _checkInterval;

    private static readonly ActivitySource _activitySource = new("RetailPulse.Alerts");

    public ProactiveAlertService(
        SqliteAlertService alertStore,
        IHubContext<TelemetryHub> hubContext,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ProactiveAlertService> logger)
    {
        _alertStore = alertStore;
        _hubContext = hubContext;
        _mcpClient = httpClientFactory.CreateClient("McpServer");
        _logger = logger;

        var intervalMinutes = configuration.GetValue("Alerts:CheckIntervalMinutes", 5);
        _checkInterval = TimeSpan.FromMinutes(intervalMinutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProactiveAlertService started — checking every {Interval}", _checkInterval);

        // Initial delay so the rest of the app can start up
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        using var timer = new PeriodicTimer(_checkInterval);

        // Run once immediately, then on each tick
        await RunCheckCycleAsync(stoppingToken);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunCheckCycleAsync(stoppingToken);
        }
    }

    private async Task RunCheckCycleAsync(CancellationToken ct)
    {
        using var activity = _activitySource.StartActivity("alert.check_cycle");

        try
        {
            var alerts = await DetectAnomaliesAsync(ct);
            activity?.SetTag("alerts.detected", alerts.Count);

            if (alerts.Count > 0)
            {
                _logger.LogInformation("Detected {Count} new alert(s)", alerts.Count);

                foreach (var alert in alerts)
                {
                    // Persist and push
                    _alertStore.PersistAlert(alert);

                    await _hubContext.Clients.All.SendAsync("alert_fired", new
                    {
                        id = alert.Id,
                        type = alert.Type,
                        severity = alert.Severity,
                        title = alert.Title,
                        description = alert.Description,
                        brand = alert.Brand,
                        region = alert.Region,
                        recommendedAction = alert.RecommendedAction,
                        detectedAt = alert.DetectedAt,
                        metadata = alert.Metadata
                    }, ct);

                    _logger.LogInformation(
                        "Alert fired: [{Severity}] {Type} — {Brand}/{Region}: {Title}",
                        alert.Severity, alert.Type, alert.Brand, alert.Region, alert.Title);
                }
            }
            else
            {
                _logger.LogDebug("No new alerts detected this cycle");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during alert check cycle");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        }
    }

    // ── Anomaly detection ────────────────────────────────────────────────

    private async Task<List<Alert>> DetectAnomaliesAsync(CancellationToken ct)
    {
        var demandData = await FetchDemandDataAsync(ct);
        if (demandData is null || demandData.Count == 0)
        {
            _logger.LogDebug("No demand data available for anomaly detection");
            return [];
        }

        var alerts = new List<Alert>();
        var now = DateTimeOffset.UtcNow;

        foreach (var (key, points) in demandData)
        {
            var parts = key.Split('|');
            if (parts.Length < 2) continue;
            var brand = parts[0];
            var region = parts[1];

            if (points.Count < 14) continue; // need at least 2 weeks of data

            // Split into current (last 7 days) and baseline (8-37 days ago)
            var sortedPoints = points.OrderBy(p => p.Date).ToList();
            var maxDate = sortedPoints[^1].Date;
            var currentStart = maxDate.AddDays(-6);
            var baselineEnd = maxDate.AddDays(-7);
            var baselineStart = maxDate.AddDays(-37);

            var currentPoints = sortedPoints.Where(p => p.Date >= currentStart).ToList();
            var baselinePoints = sortedPoints.Where(p => p.Date >= baselineStart && p.Date <= baselineEnd).ToList();

            if (currentPoints.Count == 0 || baselinePoints.Count == 0) continue;

            var currentAvg = currentPoints.Average(p => p.Volume);
            var baselineAvg = baselinePoints.Average(p => p.Volume);

            if (baselineAvg <= 0) continue;

            var pctChange = (currentAvg - baselineAvg) / baselineAvg * 100;

            // Demand spike: > 20% above baseline
            if (pctChange > 20)
            {
                var severity = pctChange > 40 ? "high" : "medium";
                var alertType = "demand_spike";

                if (!_alertStore.IsThrottled(alertType, brand, region))
                {
                    alerts.Add(new Alert(
                        Id: $"alert-{Guid.NewGuid():N}",
                        Type: alertType,
                        Severity: severity,
                        Title: $"Demand spike detected for {brand} in {region}",
                        Description: $"Current 7-day average volume is {pctChange:F1}% above the trailing 30-day baseline ({currentAvg:F0} vs {baselineAvg:F0}).",
                        Brand: brand,
                        Region: region,
                        RecommendedAction: severity == "high"
                            ? $"Immediately review inventory levels for {brand} in {region}. Consider expediting replenishment orders."
                            : $"Monitor {brand} inventory in {region} and prepare for potential stock increase.",
                        DetectedAt: now,
                        Metadata: new Dictionary<string, object>
                        {
                            ["pctChange"] = Math.Round(pctChange, 1),
                            ["currentAvg"] = Math.Round(currentAvg, 0),
                            ["baselineAvg"] = Math.Round(baselineAvg, 0),
                            ["currentPeriodDays"] = currentPoints.Count,
                            ["baselinePeriodDays"] = baselinePoints.Count
                        }
                    ));
                }
            }

            // Supply drop: > 15% below baseline
            if (pctChange < -15)
            {
                var severity = pctChange < -30 ? "high" : "medium";
                var alertType = "supply_drop";

                if (!_alertStore.IsThrottled(alertType, brand, region))
                {
                    alerts.Add(new Alert(
                        Id: $"alert-{Guid.NewGuid():N}",
                        Type: alertType,
                        Severity: severity,
                        Title: $"Supply drop detected for {brand} in {region}",
                        Description: $"Current 7-day average volume is {Math.Abs(pctChange):F1}% below the trailing 30-day baseline ({currentAvg:F0} vs {baselineAvg:F0}).",
                        Brand: brand,
                        Region: region,
                        RecommendedAction: severity == "high"
                            ? $"Urgent: investigate supply chain disruption for {brand} in {region}. Check distributor fulfillment and warehouse levels."
                            : $"Review {brand} supply pipeline in {region}. Possible distribution slowdown.",
                        DetectedAt: now,
                        Metadata: new Dictionary<string, object>
                        {
                            ["pctChange"] = Math.Round(pctChange, 1),
                            ["currentAvg"] = Math.Round(currentAvg, 0),
                            ["baselineAvg"] = Math.Round(baselineAvg, 0)
                        }
                    ));
                }
            }

            // Trend reversal: 7-day trend sign differs from 30-day trend sign + magnitude > 10%
            if (currentPoints.Count >= 3 && baselinePoints.Count >= 7)
            {
                var trend7 = CalculateLinearTrend(currentPoints);
                var trend30 = CalculateLinearTrend(baselinePoints);

                if (Math.Sign(trend7) != Math.Sign(trend30) && Math.Abs(pctChange) > 10)
                {
                    var alertType = "trend_reversal";

                    if (!_alertStore.IsThrottled(alertType, brand, region))
                    {
                        var direction = trend7 > 0 ? "upward" : "downward";
                        var previousDirection = trend30 > 0 ? "upward" : "downward";

                        alerts.Add(new Alert(
                            Id: $"alert-{Guid.NewGuid():N}",
                            Type: alertType,
                            Severity: "medium",
                            Title: $"Trend reversal for {brand} in {region}",
                            Description: $"7-day trend is {direction} while the 30-day trend was {previousDirection}. Volume shift: {pctChange:F1}%.",
                            Brand: brand,
                            Region: region,
                            RecommendedAction: $"Investigate the trend change for {brand} in {region}. Review recent promotions, competitive actions, or seasonal factors.",
                            DetectedAt: now,
                            Metadata: new Dictionary<string, object>
                            {
                                ["pctChange"] = Math.Round(pctChange, 1),
                                ["trend7Day"] = Math.Round(trend7, 4),
                                ["trend30Day"] = Math.Round(trend30, 4),
                                ["direction"] = direction,
                                ["previousDirection"] = previousDirection
                            }
                        ));
                    }
                }
            }
        }

        return alerts;
    }

    /// <summary>Simple linear regression slope over a series of points.</summary>
    private static double CalculateLinearTrend(List<(DateOnly Date, double Volume)> points)
    {
        if (points.Count < 2) return 0;

        var n = points.Count;
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;

        for (int i = 0; i < n; i++)
        {
            sumX += i;
            sumY += points[i].Volume;
            sumXY += i * points[i].Volume;
            sumX2 += i * i;
        }

        var denominator = (n * sumX2) - (sumX * sumX);
        return Math.Abs(denominator) < 0.0001 ? 0 : ((n * sumXY) - (sumX * sumY)) / denominator;
    }

    // ── Data fetching ────────────────────────────────────────────────────

    /// <summary>
    /// Fetch demand data from the MCP server for all brands and regions.
    /// Returns data grouped by "Brand|Region" with daily volume points.
    /// </summary>
    private async Task<Dictionary<string, List<(DateOnly Date, double Volume)>>?> FetchDemandDataAsync(CancellationToken ct)
    {
        try
        {
            // Fetch historical demand for all brands — the API returns weekly aggregated data
            var response = await _mcpClient.GetAsync("/api/demand-risks?brand=&region=", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch demand data from MCP server: {StatusCode}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var seriesData = new Dictionary<string, List<(DateOnly Date, double Volume)>>();

            // The demand-risks endpoint returns { risks: [...] } — but we need raw data.
            // Instead, call the historical-demand endpoint which returns weekly data.
            // Let's use the generic endpoint without filters to get all data.
            var histResponse = await _mcpClient.GetAsync("/api/historical-demand?brand=&region=National&channel=All", ct);
            if (!histResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch historical demand: {StatusCode}", histResponse.StatusCode);
                return null;
            }

            var histJson = await histResponse.Content.ReadAsStringAsync(ct);
            using var histDoc = JsonDocument.Parse(histJson);

            // Parse the weekly data from the historical demand response
            if (histDoc.RootElement.TryGetProperty("weekly_data", out var weeklyData) && weeklyData.ValueKind == JsonValueKind.Array)
            {
                foreach (var week in weeklyData.EnumerateArray())
                {
                    var brand = week.TryGetProperty("brand", out var bProp) ? bProp.GetString() : null;
                    var region = week.TryGetProperty("region", out var rProp) ? rProp.GetString() : null;
                    var weekStart = week.TryGetProperty("weekStart", out var dProp) ? dProp.GetString() : null;
                    var volume = week.TryGetProperty("volume", out var vProp) ? vProp.GetDouble() : 0;

                    if (brand is null || region is null || weekStart is null) continue;
                    if (!DateOnly.TryParse(weekStart, out var date)) continue;

                    var key = $"{brand}|{region}";
                    if (!seriesData.ContainsKey(key))
                        seriesData[key] = [];
                    seriesData[key].Add((date, volume));
                }
            }

            // If the weekly_data format didn't work, try parsing as per-brand data sections
            if (seriesData.Count == 0 && histDoc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var brand = item.TryGetProperty("brand", out var bProp) ? bProp.GetString() : null;
                    var region = item.TryGetProperty("region", out var rProp) ? rProp.GetString() : null;
                    var dateStr = item.TryGetProperty("weekStart", out var dProp) ? dProp.GetString()
                                : item.TryGetProperty("date", out var d2Prop) ? d2Prop.GetString() : null;
                    var volume = item.TryGetProperty("volume", out var vProp) ? vProp.GetDouble()
                               : item.TryGetProperty("avgVolume", out var v2Prop) ? v2Prop.GetDouble() : 0;

                    if (brand is null || dateStr is null) continue;
                    region ??= "National";
                    if (!DateOnly.TryParse(dateStr, out var date)) continue;

                    var key = $"{brand}|{region}";
                    if (!seriesData.ContainsKey(key))
                        seriesData[key] = [];
                    seriesData[key].Add((date, volume));
                }
            }

            _logger.LogDebug("Fetched demand data for {Count} brand/region combinations", seriesData.Count);
            return seriesData;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching demand data for anomaly detection");
            return null;
        }
    }
}

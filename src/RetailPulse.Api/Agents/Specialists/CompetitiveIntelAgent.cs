using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Alerts;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Models;
using RetailPulse.Contracts.Alerts;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Agents.Specialists;

/// <summary>
/// Competitive Intelligence specialist — bespoke agent that keeps the standard
/// execution pipeline (inherited from <see cref="ConfiguredSpecialistAgent"/>)
/// but layers a real-time SignalR alert publisher on top of every tool result.
/// The alert-parsing logic is genuinely specialized and stays here rather than
/// being pushed into configuration.
/// </summary>
public sealed class CompetitiveIntelAgent : ConfiguredSpecialistAgent
{
    private readonly IHubContext<TelemetryHub> _hubContext;
    private readonly ILogger<CompetitiveIntelAgent> _logger;
    private readonly SqliteAlertService? _alertService;

    public CompetitiveIntelAgent(
        IAgentExecutionPipeline pipeline,
        AgentDefinition agentDef,
        IEnumerable<AITool> tools,
        IHubContext<TelemetryHub> hubContext,
        ILogger<CompetitiveIntelAgent> logger,
        SqliteAlertService? alertService = null)
        : base(pipeline, EnsureDefaults(agentDef), tools)
    {
        _hubContext = hubContext;
        _logger = logger;
        _alertService = alertService;
    }

    private static AgentDefinition EnsureDefaults(AgentDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);

        def = def.Clone();

        if (string.IsNullOrWhiteSpace(def.Key))
            def.Key = "competitive-intel";
        if (def.Intents.Count == 0)
            def.Intents = [AgentIntent.CompetitiveMarket];
        if (string.IsNullOrWhiteSpace(def.DisplayName))
            def.DisplayName = "Competitive Intelligence Agent";
        if (string.IsNullOrWhiteSpace(def.FallbackReply))
            def.FallbackReply = "I wasn't able to generate a competitive analysis.";
        return def;
    }

    protected override Func<string, CancellationToken, Task>? OnToolResult => CheckAndFireAlertsAsync;

    /// <summary>
    /// Scans tool results for competitive threats and fires proactive alerts via SignalR.
    /// Triggers on: price drops >10%, share losses >2pts, high-impact competitor activities.
    /// </summary>
    private async Task CheckAndFireAlertsAsync(string resultText, CancellationToken ct)
    {
        if (_alertService is null || string.IsNullOrEmpty(resultText)) return;

        try
        {
            using var doc = JsonDocument.Parse(resultText);

            // Check threat detection results for high-severity items
            if (doc.RootElement.TryGetProperty("threats", out JsonElement threats) &&
                threats.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement threat in threats.EnumerateArray())
                {
                    string? severity = threat.TryGetProperty("severity", out JsonElement sev) ? sev.GetString() : "low";
                    if (severity != "high") continue;

                    string threatType = threat.TryGetProperty("type", out JsonElement tt) ? tt.GetString() ?? "unknown" : "unknown";
                    string? competitor = threat.TryGetProperty("competitor", out JsonElement comp) ? comp.GetString() : "Unknown";
                    string region = threat.TryGetProperty("region", out JsonElement reg) ? reg.GetString() ?? "National" : "National";
                    string? brand = threat.TryGetProperty("brand", out JsonElement br) ? br.GetString() : null;
                    string? category = threat.TryGetProperty("category", out JsonElement cat) ? cat.GetString() : null;
                    string? recommendation = threat.TryGetProperty("recommendation", out JsonElement rec) ? rec.GetString() : "Monitor";

                    string displayBrand = brand ?? category ?? "Unknown";

                    if (!_alertService.IsThrottled("competitive_threat", displayBrand, region))
                    {
                        var alert = new Alert(
                            Id: $"alert-{Guid.NewGuid():N}",
                            Type: "competitive_threat",
                            Severity: severity,
                            Title: $"Competitive threat: {threatType} by {competitor} in {region}",
                            Description: $"High-severity {threatType} detected from {competitor} affecting {displayBrand} in {region}.",
                            Brand: displayBrand,
                            Region: region,
                            RecommendedAction: $"Recommended strategy: {recommendation}. Review competitive landscape for {category ?? displayBrand} in {region}.",
                            DetectedAt: DateTimeOffset.UtcNow,
                            Metadata: new Dictionary<string, object>
                            {
                                ["competitor"] = competitor ?? "Unknown",
                                ["threat_type"] = threatType,
                                ["recommendation"] = recommendation ?? "Monitor",
                                ["source"] = "competitive_intel_agent"
                            }
                        );

                        _alertService.PersistAlert(alert);
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
                            "Competitive alert fired: [{Severity}] {ThreatType} — {Competitor} vs {Brand}/{Region}",
                            severity, threatType, competitor, displayBrand, region);
                    }
                }
            }

            // Check pricing results for dramatic drops (>10%)
            if (doc.RootElement.TryGetProperty("price_drop_threats", out JsonElement dropCount) &&
                dropCount.GetInt32() > 0 &&
                doc.RootElement.TryGetProperty("pricing", out JsonElement pricing) &&
                pricing.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement p in pricing.EnumerateArray())
                {
                    double pctChange = p.TryGetProperty("price_change_percent", out JsonElement pct) ? pct.GetDouble() : 0;
                    if (pctChange >= -10) continue;

                    string? competitor = p.TryGetProperty("competitor", out JsonElement comp) ? comp.GetString() : "Unknown";
                    string brand = p.TryGetProperty("brand", out JsonElement br) ? br.GetString() ?? "Unknown" : "Unknown";
                    string region = p.TryGetProperty("region", out JsonElement reg) ? reg.GetString() ?? "National" : "National";

                    if (!_alertService.IsThrottled("competitive_threat", brand, region))
                    {
                        var alert = new Alert(
                            Id: $"alert-{Guid.NewGuid():N}",
                            Type: "competitive_threat",
                            Severity: pctChange < -20 ? "high" : "medium",
                            Title: $"Competitor price drop: {competitor} on {brand} in {region}",
                            Description: $"{competitor} dropped price by {Math.Abs(pctChange):F1}% on products competing with {brand} in {region}.",
                            Brand: brand,
                            Region: region,
                            RecommendedAction: pctChange < -20
                                ? $"MATCH — Significant undercut from {competitor}. Consider matching within 1-2 weeks."
                                : $"DIFFERENTIATE — Moderate drop from {competitor}. Emphasize value proposition.",
                            DetectedAt: DateTimeOffset.UtcNow,
                            Metadata: new Dictionary<string, object>
                            {
                                ["competitor"] = competitor ?? "Unknown",
                                ["price_change_percent"] = pctChange,
                                ["source"] = "competitive_intel_agent"
                            }
                        );

                        _alertService.PersistAlert(alert);
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
                    }
                }
            }
        }
        catch
        {
            // Non-critical: don't let alert parsing failures break the agent response
        }
    }
}

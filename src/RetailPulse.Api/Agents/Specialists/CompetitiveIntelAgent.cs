using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Alerts;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Alerts;
using RetailPulse.Contracts.Routing;
using ChatRequest = RetailPulse.Contracts.ChatRequest;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Api.Agents.Specialists;

/// <summary>
/// Competitive Intelligence specialist — monitors competitor activities
/// (pricing, promotions, market share) and provides strategic recommendations.
/// Integrates with proactive alert system for real-time threat detection.
/// Temperature 0.4 balances analytical precision with creative strategy.
/// </summary>
public class CompetitiveIntelAgent : ISpecialistAgent
{
    private readonly IAgentExecutionPipeline _pipeline;
    private readonly AgentDefinition _agentDef;
    public string Model => _agentDef.Model;
    private readonly IEnumerable<AITool> _tools;
    private readonly IHubContext<TelemetryHub> _hubContext;
    private readonly ILogger<CompetitiveIntelAgent> _logger;
    private readonly SqliteAlertService? _alertService;

    public string Key => "competitive-intel";
    public string DisplayName => "Competitive Intelligence Agent";
    public IReadOnlyList<string> SupportedIntents { get; } =
    [
        AgentIntent.CompetitiveMarket
    ];

    public CompetitiveIntelAgent(
        IAgentExecutionPipeline pipeline,
        AgentDefinition agentDef,
        IEnumerable<AITool> tools,
        IHubContext<TelemetryHub> hubContext,
        ILogger<CompetitiveIntelAgent> logger,
        SqliteAlertService? alertService = null)
    {
        _pipeline = pipeline;
        _agentDef = agentDef;
        _tools = tools;
        _hubContext = hubContext;
        _logger = logger;
        _alertService = alertService;
    }

    public Task<ChatResponse> HandleAsync(ChatRequest request, CancellationToken ct = default)
    {
        var context = new AgentExecutionContext
        {
            AgentName = _agentDef.Name,
            SystemPrompt = _agentDef.SystemPrompt,
            Temperature = (float)_agentDef.Temperature,
            ModelName = _agentDef.Model,
            Request = request,
            Tools = _tools,
            FallbackReply = "I wasn't able to generate a competitive analysis.",
            OnToolResult = CheckAndFireAlertsAsync
        };

        return _pipeline.ExecuteAsync(context, ct);
    }

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
            if (doc.RootElement.TryGetProperty("threats", out var threats) &&
                threats.ValueKind == JsonValueKind.Array)
            {
                foreach (var threat in threats.EnumerateArray())
                {
                    var severity = threat.TryGetProperty("severity", out var sev) ? sev.GetString() : "low";
                    if (severity != "high") continue;

                    var threatType = threat.TryGetProperty("type", out var tt) ? tt.GetString() ?? "unknown" : "unknown";
                    var competitor = threat.TryGetProperty("competitor", out var comp) ? comp.GetString() : "Unknown";
                    var region = threat.TryGetProperty("region", out var reg) ? reg.GetString() ?? "National" : "National";
                    var brand = threat.TryGetProperty("brand", out var br) ? br.GetString() : null;
                    var category = threat.TryGetProperty("category", out var cat) ? cat.GetString() : null;
                    var recommendation = threat.TryGetProperty("recommendation", out var rec) ? rec.GetString() : "Monitor";

                    var displayBrand = brand ?? category ?? "Unknown";

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
            if (doc.RootElement.TryGetProperty("price_drop_threats", out var dropCount) &&
                dropCount.GetInt32() > 0 &&
                doc.RootElement.TryGetProperty("pricing", out var pricing) &&
                pricing.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pricing.EnumerateArray())
                {
                    var pctChange = p.TryGetProperty("price_change_percent", out var pct) ? pct.GetDouble() : 0;
                    if (pctChange >= -10) continue;

                    var competitor = p.TryGetProperty("competitor", out var comp) ? comp.GetString() : "Unknown";
                    var brand = p.TryGetProperty("brand", out var br) ? br.GetString() ?? "Unknown" : "Unknown";
                    var region = p.TryGetProperty("region", out var reg) ? reg.GetString() ?? "National" : "National";

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

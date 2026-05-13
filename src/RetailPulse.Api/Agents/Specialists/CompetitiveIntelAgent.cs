using System.ClientModel;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Alerts;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Middleware;
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
    private readonly IChatClient _chatClient;
    private readonly AgentDefinition _agentDef;
    private readonly IHubContext<TelemetryHub> _hubContext;
    private readonly IEnumerable<AITool> _tools;
    private readonly ILogger<CompetitiveIntelAgent> _logger;
    private readonly IConfiguration _configuration;
    private readonly SqliteAlertService? _alertService;

    public string Key => "competitive-intel";
    public string DisplayName => "Competitive Intelligence Agent";
    public IReadOnlyList<string> SupportedIntents { get; } =
    [
        AgentIntent.CompetitiveMarket
    ];

    public CompetitiveIntelAgent(
        IChatClient chatClient,
        AgentDefinition agentDef,
        IHubContext<TelemetryHub> hubContext,
        IEnumerable<AITool> tools,
        ILogger<CompetitiveIntelAgent> logger,
        IConfiguration configuration,
        SqliteAlertService? alertService = null)
    {
        _chatClient = chatClient;
        _agentDef = agentDef;
        _hubContext = hubContext;
        _tools = tools;
        _logger = logger;
        _configuration = configuration;
        _alertService = alertService;
    }

    public async Task<ChatResponse> HandleAsync(ChatRequest request, CancellationToken ct = default)
    {
        var sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
        var collector = new TelemetryCollector(_hubContext, sessionId);

        var chatOptions = new ChatOptions
        {
            Temperature = (float)_agentDef.Temperature,
            Tools = _tools.ToList()
        };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _agentDef.SystemPrompt)
        };

        if (request.History is { Count: > 0 })
        {
            const int maxTurns = 10;
            var historyMessages = request.History.Count > maxTurns * 2
                ? request.History.Skip(request.History.Count - (maxTurns * 2)).ToList()
                : request.History;

            foreach (var historyMessage in historyMessages)
            {
                var role = string.Equals(historyMessage.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? ChatRole.Assistant
                    : ChatRole.User;
                messages.Add(new ChatMessage(role, historyMessage.Content));
            }
        }

        messages.Add(new(ChatRole.User, request.Message));

        var sw = Stopwatch.StartNew();
        using var thoughtActivity = AgentTelemetry.StartAgentThought(_agentDef.Name, request.Message);

        Microsoft.Extensions.AI.ChatResponse response;

        try
        {
            response = await _chatClient.GetResponseAsync(messages, chatOptions, ct);
        }
        catch (ClientResultException ex) when (ex.Status == 429)
        {
            var failureDurationMs = sw.ElapsedMilliseconds;
            thoughtActivity?.SetTag("agent.duration_ms", failureDurationMs);
            _logger.LogWarning(ex, "Competitive intel agent rate-limited after {DurationMs}ms", failureDurationMs);
            return new ChatResponse(
                "⏳ The AI service is temporarily rate-limited. Please wait a moment and try again.",
                sessionId, [], null, failureDurationMs);
        }
        catch (Exception ex)
        {
            var failureDurationMs = sw.ElapsedMilliseconds;
            thoughtActivity?.SetTag("agent.duration_ms", failureDurationMs);
            _logger.LogError(ex, "Competitive intel agent failed after {DurationMs}ms for session {SessionId}",
                failureDurationMs, sessionId);
            return new ChatResponse(
                "⚠️ Something went wrong while analyzing competitive intelligence. Please try again.",
                sessionId, [], null, failureDurationMs);
        }

        var thoughtDurationMs = sw.ElapsedMilliseconds;
        thoughtActivity?.SetTag("agent.duration_ms", thoughtDurationMs);

        var inputTokens = (int)(response.Usage?.InputTokenCount ?? 0);
        var outputTokens = (int)(response.Usage?.OutputTokenCount ?? 0);
        var totalTokens = (int)(response.Usage?.TotalTokenCount ?? (inputTokens + outputTokens));

        await collector.RecordSpanAsync(
            _agentDef.Name, "thought",
            $"Processing: {request.Message[..Math.Min(100, request.Message.Length)]}",
            thoughtDurationMs,
            inputTokens > 0 ? (int?)inputTokens : null,
            outputTokens > 0 ? (int?)outputTokens : null);

        var callIdToName = new Dictionary<string, string>();
        foreach (var msg in response.Messages)
        foreach (var content in msg.Contents)
        {
            if (content is FunctionCallContent fc && fc.CallId != null)
                callIdToName[fc.CallId] = fc.Name;
        }

        var toolCount = callIdToName.Count;
        var perToolMs = toolCount > 0 ? thoughtDurationMs / toolCount : 0;

        foreach (var msg in response.Messages)
        foreach (var content in msg.Contents)
        {
            if (content is FunctionCallContent toolCall)
            {
                using var toolActivity = AgentTelemetry.StartToolCall(
                    toolCall.Name,
                    System.Text.Json.JsonSerializer.Serialize(toolCall.Arguments));
                await collector.RecordSpanAsync(
                    toolCall.Name, "tool_call",
                    $"Calling {toolCall.Name} with {System.Text.Json.JsonSerializer.Serialize(toolCall.Arguments)}",
                    perToolMs);
            }
            else if (content is FunctionResultContent toolResult)
            {
                var toolName = callIdToName.GetValueOrDefault(toolResult.CallId ?? "", toolResult.CallId ?? "unknown");
                var resultText = toolResult.Result?.ToString() ?? "";
                using var resultActivity = AgentTelemetry.StartToolResult(toolName, resultText.Length);
                await collector.RecordSpanAsync(
                    toolName, "tool_result",
                    resultText[..Math.Min(200, resultText.Length)],
                    0);

                // Fire proactive alerts for high-severity threats detected in tool results
                await CheckAndFireAlertsAsync(resultText, ct);
            }
        }

        var postProcessStart = sw.ElapsedMilliseconds;
        var reply = response.Text ?? "I wasn't able to generate a competitive analysis.";

        var charts = ExtractChartSpecs(response);

        using var responseActivity = AgentTelemetry.StartAgentResponse(_agentDef.Name);
        var responseDurationMs = sw.ElapsedMilliseconds - postProcessStart;
        await collector.RecordSpanAsync(
            _agentDef.Name, "response",
            reply[..Math.Min(200, reply.Length)],
            responseDurationMs);

        var totalDurationMs = sw.ElapsedMilliseconds;
        var tokenUsage = BuildTokenUsage(inputTokens, outputTokens, totalTokens);

        _logger.LogInformation("Competitive intel agent responded in {DurationMs}ms with {SpanCount} spans, {ChartCount} charts, {TokenCount} tokens",
            totalDurationMs, collector.Spans.Count, charts.Count, totalTokens);

        return new ChatResponse(
            reply, sessionId, collector.Spans.ToList(),
            charts.Any() ? charts : null,
            totalDurationMs, tokenUsage);
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
            using var doc = System.Text.Json.JsonDocument.Parse(resultText);

            // Check threat detection results for high-severity items
            if (doc.RootElement.TryGetProperty("threats", out var threats) &&
                threats.ValueKind == System.Text.Json.JsonValueKind.Array)
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
                            id = alert.Id, type = alert.Type, severity = alert.Severity,
                            title = alert.Title, description = alert.Description,
                            brand = alert.Brand, region = alert.Region,
                            recommendedAction = alert.RecommendedAction,
                            detectedAt = alert.DetectedAt, metadata = alert.Metadata
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
                pricing.ValueKind == System.Text.Json.JsonValueKind.Array)
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
                            id = alert.Id, type = alert.Type, severity = alert.Severity,
                            title = alert.Title, description = alert.Description,
                            brand = alert.Brand, region = alert.Region,
                            recommendedAction = alert.RecommendedAction,
                            detectedAt = alert.DetectedAt, metadata = alert.Metadata
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

    private static List<ChartSpec> ExtractChartSpecs(Microsoft.Extensions.AI.ChatResponse chatResponse)
    {
        var charts = new List<ChartSpec>();
        foreach (var msg in chatResponse.Messages)
        foreach (var content in msg.Contents)
        {
            if (content is FunctionResultContent toolResult)
            {
                var resultText = toolResult.Result?.ToString();
                if (!string.IsNullOrEmpty(resultText))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(resultText);
                        if (doc.RootElement.TryGetProperty("status", out var status) &&
                            status.GetString() == "success" &&
                            doc.RootElement.TryGetProperty("chart", out var chartElement))
                        {
                            var chart = System.Text.Json.JsonSerializer.Deserialize<ChartSpec>(
                                chartElement.GetRawText(),
                                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (chart != null) charts.Add(chart);
                        }
                    }
                    catch { /* Not a chart result */ }
                }
            }
        }
        return charts;
    }

    internal TokenUsage BuildTokenUsage(int inputTokens, int outputTokens, int totalTokens)
    {
        decimal? cost = null;
        var modelName = _agentDef.Model;
        var pricingSection = _configuration.GetSection($"TokenPricing:{modelName}");
        if (pricingSection.Exists())
        {
            var inputRate = pricingSection.GetValue<decimal>("InputPerMillion");
            var outputRate = pricingSection.GetValue<decimal>("OutputPerMillion");
            cost = (inputTokens * inputRate / 1_000_000m) + (outputTokens * outputRate / 1_000_000m);
        }
        return new TokenUsage(inputTokens, outputTokens, totalTokens, cost);
    }
}

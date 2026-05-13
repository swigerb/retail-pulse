using System.ClientModel;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Approval;
using RetailPulse.Contracts.Routing;
using ChatRequest = RetailPulse.Contracts.ChatRequest;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Api.Agents.Specialists;

/// <summary>
/// Promotion Planning specialist — handles promotion/trade queries:
/// promo history analysis, lift calculations, timing evaluation, ROI estimation,
/// and campaign approval gating. Uses its own tool set and lower temperature (0.3).
/// </summary>
public class PromoPlanningAgent : ISpecialistAgent
{
    private readonly IChatClient _chatClient;
    private readonly AgentDefinition _agentDef;
    private readonly IHubContext<TelemetryHub> _hubContext;
    private readonly IEnumerable<AITool> _tools;
    private readonly ILogger<PromoPlanningAgent> _logger;
    private readonly IConfiguration _configuration;
    private readonly IApprovalGate? _approvalGate;

    public string Key => "promo-planning";
    public string DisplayName => "Promotion Planning Agent";
    public IReadOnlyList<string> SupportedIntents { get; } =
    [
        AgentIntent.PromotionTrade
    ];

    public PromoPlanningAgent(
        IChatClient chatClient,
        AgentDefinition agentDef,
        IHubContext<TelemetryHub> hubContext,
        IEnumerable<AITool> tools,
        ILogger<PromoPlanningAgent> logger,
        IConfiguration configuration,
        IApprovalGate? approvalGate = null)
    {
        _chatClient = chatClient;
        _agentDef = agentDef;
        _hubContext = hubContext;
        _tools = tools;
        _logger = logger;
        _configuration = configuration;
        _approvalGate = approvalGate;
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
            _logger.LogWarning(ex, "Promo planning agent rate-limited after {DurationMs}ms", failureDurationMs);
            return new ChatResponse(
                "⏳ The AI service is temporarily rate-limited. Please wait a moment and try again.",
                sessionId, [], null, failureDurationMs);
        }
        catch (Exception ex)
        {
            var failureDurationMs = sw.ElapsedMilliseconds;
            thoughtActivity?.SetTag("agent.duration_ms", failureDurationMs);
            _logger.LogError(ex, "Promo planning agent failed after {DurationMs}ms for session {SessionId}",
                failureDurationMs, sessionId);
            return new ChatResponse(
                "⚠️ Something went wrong while evaluating the promotion. Please try again.",
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
            }
        }

        var postProcessStart = sw.ElapsedMilliseconds;
        var reply = response.Text ?? "I wasn't able to generate a promotion analysis.";

        var charts = ExtractChartSpecs(response);

        using var responseActivity = AgentTelemetry.StartAgentResponse(_agentDef.Name);
        var responseDurationMs = sw.ElapsedMilliseconds - postProcessStart;
        await collector.RecordSpanAsync(
            _agentDef.Name, "response",
            reply[..Math.Min(200, reply.Length)],
            responseDurationMs);

        var totalDurationMs = sw.ElapsedMilliseconds;
        var tokenUsage = BuildTokenUsage(inputTokens, outputTokens, totalTokens);

        _logger.LogInformation("Promo planning agent responded in {DurationMs}ms with {SpanCount} spans, {ChartCount} charts, {TokenCount} tokens",
            totalDurationMs, collector.Spans.Count, charts.Count, totalTokens);

        return new ChatResponse(
            reply, sessionId, collector.Spans.ToList(),
            charts.Any() ? charts : null,
            totalDurationMs, tokenUsage);
    }

    /// <summary>
    /// Checks if the given spend amount triggers the approval gate for high-spend campaigns.
    /// </summary>
    public async Task<ApprovalResult?> CheckApprovalAsync(
        double spend, double roi, string userId, string description, CancellationToken ct = default)
    {
        if (_approvalGate == null) return null;

        var requiresApproval = spend > 500_000 || (spend > 100_000 && roi < 10);
        if (!requiresApproval) return null;

        var urgency = spend > 500_000 ? "High" : "Medium";
        var impact = $"Campaign spend: ${spend:N0}, Expected ROI: {roi:F1}%";

        var request = await _approvalGate.RequestApprovalAsync(new ApprovalContext(
            AgentId: Key,
            UserId: userId,
            Action: description,
            Impact: impact,
            Urgency: urgency,
            Reasoning: $"High-spend promotion requires approval. Spend=${spend:N0}, ROI={roi:F1}%"
        ), ct);

        return await _approvalGate.GetResultAsync(request.RequestId, ct);
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

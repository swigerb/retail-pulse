using System.ClientModel;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;
using ChatRequest = RetailPulse.Contracts.ChatRequest;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Api.Agents.Specialists;

/// <summary>
/// Supply Chain specialist — handles inventory levels, supply disruptions,
/// fulfillment rates, and overall supply health assessments.
/// Uses its own tool set and lower temperature (0.3) for analytical precision.
/// </summary>
public class SupplyChainAgent : ISpecialistAgent
{
    private readonly IChatClient _chatClient;
    private readonly AgentDefinition _agentDef;
    private readonly IHubContext<TelemetryHub> _hubContext;
    private readonly IEnumerable<AITool> _tools;
    private readonly ILogger<SupplyChainAgent> _logger;
    private readonly IConfiguration _configuration;

    public string Key => "supply-chain";
    public string DisplayName => "Supply Chain Agent";
    public IReadOnlyList<string> SupportedIntents { get; } =
    [
        AgentIntent.SupplyShipments
    ];

    public SupplyChainAgent(
        IChatClient chatClient,
        AgentDefinition agentDef,
        IHubContext<TelemetryHub> hubContext,
        IEnumerable<AITool> tools,
        ILogger<SupplyChainAgent> logger,
        IConfiguration configuration)
    {
        _chatClient = chatClient;
        _agentDef = agentDef;
        _hubContext = hubContext;
        _tools = tools;
        _logger = logger;
        _configuration = configuration;
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
            thoughtActivity?.SetTag("error.status_code", ex.Status);

            _logger.LogWarning(ex,
                "Supply chain agent rate-limited after {DurationMs}ms. Status: {Status}",
                failureDurationMs, ex.Status);

            return new ChatResponse(
                "⏳ The AI service is temporarily rate-limited. Please wait a moment and try again.",
                sessionId, [], null, failureDurationMs);
        }
        catch (Exception ex)
        {
            var failureDurationMs = sw.ElapsedMilliseconds;
            thoughtActivity?.SetTag("agent.duration_ms", failureDurationMs);
            thoughtActivity?.SetTag("error.type", ex.GetType().FullName);

            _logger.LogError(ex,
                "Supply chain agent failed after {DurationMs}ms for session {SessionId}",
                failureDurationMs, sessionId);

            return new ChatResponse(
                "⚠️ Something went wrong while analyzing the supply chain. Please try again.",
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
        {
            foreach (var content in msg.Contents)
            {
                if (content is FunctionCallContent fc && fc.CallId != null)
                    callIdToName[fc.CallId] = fc.Name;
            }
        }

        var toolCount = callIdToName.Count;
        var perToolMs = toolCount > 0 ? thoughtDurationMs / toolCount : 0;

        foreach (var msg in response.Messages)
        {
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
        }

        var postProcessStart = sw.ElapsedMilliseconds;
        var reply = response.Text ?? "I wasn't able to generate a supply chain analysis.";

        var charts = ExtractChartSpecs(response);

        using var responseActivity = AgentTelemetry.StartAgentResponse(_agentDef.Name);
        var responseDurationMs = sw.ElapsedMilliseconds - postProcessStart;
        await collector.RecordSpanAsync(
            _agentDef.Name, "response",
            reply[..Math.Min(200, reply.Length)],
            responseDurationMs);

        var totalDurationMs = sw.ElapsedMilliseconds;

        var tokenUsage = BuildTokenUsage(inputTokens, outputTokens, totalTokens);

        _logger.LogInformation("Supply chain agent responded in {DurationMs}ms with {SpanCount} spans, {ChartCount} charts, {TokenCount} tokens",
            totalDurationMs, collector.Spans.Count, charts.Count, totalTokens);

        return new ChatResponse(
            reply, sessionId, collector.Spans.ToList(),
            charts.Any() ? charts : null,
            totalDurationMs, tokenUsage);
    }

    private static List<ChartSpec> ExtractChartSpecs(Microsoft.Extensions.AI.ChatResponse chatResponse)
    {
        var charts = new List<ChartSpec>();

        foreach (var msg in chatResponse.Messages)
        {
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
                                if (chart != null)
                                {
                                    charts.Add(chart);
                                }
                            }
                        }
                        catch
                        {
                            // Not a chart result
                        }
                    }
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
        else
        {
            _logger.LogWarning(
                "No TokenPricing config found for model '{ModelName}'. Cost will not be calculated.",
                modelName);
        }

        return new TokenUsage(inputTokens, outputTokens, totalTokens, cost);
    }
}

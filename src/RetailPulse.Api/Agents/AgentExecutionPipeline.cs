using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Telemetry;
using RetailPulse.Contracts;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Api.Agents;

/// <summary>
/// Default implementation of <see cref="IAgentExecutionPipeline"/>.
/// Extracts the shared execution pattern from all specialist agents:
/// message construction → LLM call → telemetry → tool spans → charts → tokens → response.
/// </summary>
public class AgentExecutionPipeline : IAgentExecutionPipeline
{
    private readonly IChatClient _chatClient;
    private readonly IHubContext<TelemetryHub> _hubContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AgentExecutionPipeline> _logger;
    private readonly RetailPulseMetrics? _metrics;

    private static readonly JsonSerializerOptions _caseInsensitiveOptions = new() { PropertyNameCaseInsensitive = true };

    public AgentExecutionPipeline(
        IChatClient chatClient,
        IHubContext<TelemetryHub> hubContext,
        IConfiguration configuration,
        ILogger<AgentExecutionPipeline> logger,
        RetailPulseMetrics? metrics = null)
    {
        _chatClient = chatClient;
        _hubContext = hubContext;
        _configuration = configuration;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task<ChatResponse> ExecuteAsync(AgentExecutionContext context, CancellationToken ct = default)
    {
        var request = context.Request;
        var sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
        var collector = new TelemetryCollector(_hubContext, sessionId);

        var chatOptions = new ChatOptions
        {
            Temperature = context.Temperature,
            Tools = [.. context.Tools]
        };

        var messages = BuildMessages(context.SystemPrompt, request);

        var sw = Stopwatch.StartNew();
        using var thoughtActivity = AgentTelemetry.StartAgentThought(context.AgentName, request.Message);

        // Emit progress: thinking phase
        _ = _hubContext.Clients.Group(sessionId).SendAsync("progress", new
        {
            sessionId,
            phase = "thinking",
            detail = $"{context.AgentName} is reasoning...",
            timestamp = DateTimeOffset.UtcNow
        }, ct);

        Microsoft.Extensions.AI.ChatResponse response;

        try
        {
            response = await _chatClient.GetResponseAsync(messages, chatOptions, ct);
        }
        catch (ClientResultException ex) when (ex.Status == 429)
        {
            return HandleRateLimitError(ex, sw, thoughtActivity, context.AgentName, sessionId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller (e.g. council timeout or request-level timeout) cancelled — propagate
            // so upstream code can handle it with its own cancellation logic.
            throw;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return HandleTimeoutError(ex, sw, thoughtActivity, context.AgentName, sessionId);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return HandleTimeoutError(ex, sw, thoughtActivity, context.AgentName, sessionId);
        }
        catch (Exception ex)
        {
            return HandleUnexpectedError(ex, sw, thoughtActivity, context.AgentName, sessionId);
        }

        var thoughtDurationMs = sw.ElapsedMilliseconds;
        thoughtActivity?.SetTag("agent.duration_ms", thoughtDurationMs);

        var (inputTokens, outputTokens, totalTokens) = ExtractTokenCounts(response);

        await collector.RecordSpanAsync(
            context.AgentName, "thought",
            $"Processing: {request.Message[..Math.Min(100, request.Message.Length)]}",
            thoughtDurationMs,
            inputTokens > 0 ? inputTokens : null,
            outputTokens > 0 ? outputTokens : null);

        await RecordToolSpansAsync(response, collector, thoughtDurationMs, context.OnToolResult, _hubContext, sessionId, ct);

        // Emit progress: synthesizing phase
        _ = _hubContext.Clients.Group(sessionId).SendAsync("progress", new
        {
            sessionId,
            phase = "synthesizing",
            detail = "Preparing response...",
            timestamp = DateTimeOffset.UtcNow
        }, ct);

        var postProcessStart = sw.ElapsedMilliseconds;
        var reply = response.Text ?? context.FallbackReply;

        var charts = ExtractChartSpecs(response);

        using var responseActivity = AgentTelemetry.StartAgentResponse(context.AgentName);
        var responseDurationMs = sw.ElapsedMilliseconds - postProcessStart;
        await collector.RecordSpanAsync(
            context.AgentName, "response",
            reply[..Math.Min(200, reply.Length)],
            responseDurationMs);

        var totalDurationMs = sw.ElapsedMilliseconds;

        var tokenUsage = BuildTokenUsage(inputTokens, outputTokens, totalTokens, context.ModelName);

        _logger.LogInformation(
            "Agent {AgentName} responded in {DurationMs}ms with {SpanCount} spans, {ChartCount} charts, {TokenCount} tokens",
            context.AgentName, totalDurationMs, collector.Spans.Count, charts.Count, totalTokens);

        _metrics?.RecordAgentExecutionDuration(context.AgentName, totalDurationMs);

        return new ChatResponse(
            reply, sessionId, [.. collector.Spans],
            charts.Count > 0 ? charts : null,
            totalDurationMs, tokenUsage);
    }

    internal static List<ChatMessage> BuildMessages(string systemPrompt, ChatRequest request)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt)
        };

        if (request.History is { Count: > 0 })
        {
            const int maxTurns = 10;
            var historyMessages = request.History.Count > maxTurns * 2
                ? [.. request.History.Skip(request.History.Count - (maxTurns * 2))]
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
        return messages;
    }

    private static (int input, int output, int total) ExtractTokenCounts(Microsoft.Extensions.AI.ChatResponse response)
    {
        var input = (int)(response.Usage?.InputTokenCount ?? 0);
        var output = (int)(response.Usage?.OutputTokenCount ?? 0);
        var total = (int)(response.Usage?.TotalTokenCount ?? (input + output));
        return (input, output, total);
    }

    private static async Task RecordToolSpansAsync(
        Microsoft.Extensions.AI.ChatResponse response,
        TelemetryCollector collector,
        long thoughtDurationMs,
        Func<string, CancellationToken, Task>? onToolResult,
        IHubContext<TelemetryHub> hubContext,
        string sessionId,
        CancellationToken ct)
    {
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
                    // Emit progress: tool_call phase
                    _ = hubContext.Clients.Group(sessionId).SendAsync("progress", new
                    {
                        sessionId,
                        phase = "tool_call",
                        detail = $"Calling {toolCall.Name}...",
                        timestamp = DateTimeOffset.UtcNow
                    }, ct);

                    using var toolActivity = AgentTelemetry.StartToolCall(
                        toolCall.Name,
                        JsonSerializer.Serialize(toolCall.Arguments));
                    await collector.RecordSpanAsync(
                        toolCall.Name, "tool_call",
                        $"Calling {toolCall.Name} with {JsonSerializer.Serialize(toolCall.Arguments)}",
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

                    if (onToolResult != null)
                        await onToolResult(resultText, ct);
                }
            }
    }

    internal static List<ChartSpec> ExtractChartSpecs(Microsoft.Extensions.AI.ChatResponse chatResponse)
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
                            using var doc = JsonDocument.Parse(resultText);
                            if (doc.RootElement.TryGetProperty("status", out var status) &&
                                status.GetString() == "success" &&
                                doc.RootElement.TryGetProperty("chart", out var chartElement))
                            {
                                var chart = JsonSerializer.Deserialize<ChartSpec>(
                                    chartElement.GetRawText(), _caseInsensitiveOptions);
                                if (chart != null)
                                    charts.Add(chart);
                            }
                        }
                        catch
                        {
                            // Not a chart result — skip
                        }
                    }
                }
            }
        return charts;
    }

    internal TokenUsage BuildTokenUsage(int inputTokens, int outputTokens, int totalTokens, string modelName)
    {
        decimal? cost = null;
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

    private ChatResponse HandleRateLimitError(
        ClientResultException ex, Stopwatch sw, Activity? thoughtActivity,
        string agentName, string sessionId)
    {
        var failureDurationMs = sw.ElapsedMilliseconds;
        thoughtActivity?.SetTag("agent.duration_ms", failureDurationMs);
        thoughtActivity?.SetTag("error.status_code", ex.Status);

        _metrics?.RecordError("rate_limit");

        _logger.LogWarning(ex,
            "Agent {AgentName} rate-limited after {DurationMs}ms. Status: {Status}",
            agentName, failureDurationMs, ex.Status);

        return new ChatResponse(
            "⏳ The AI service is temporarily rate-limited. Please wait a moment and try again.",
            sessionId, [], null, failureDurationMs);
    }

    private ChatResponse HandleTimeoutError(
        Exception ex, Stopwatch sw, Activity? thoughtActivity,
        string agentName, string sessionId)
    {
        var failureDurationMs = sw.ElapsedMilliseconds;
        thoughtActivity?.SetTag("agent.duration_ms", failureDurationMs);
        thoughtActivity?.SetTag("error.type", "timeout");

        _metrics?.RecordError("timeout");

        _logger.LogWarning(ex,
            "Agent {AgentName} timed out after {DurationMs}ms for session {SessionId}",
            agentName, failureDurationMs, sessionId);

        return new ChatResponse(
            "⏳ The request took too long to complete. This can happen with complex multi-step analyses. Please try again — if the issue persists, try a simpler question first.",
            sessionId, [], null, failureDurationMs);
    }

    private ChatResponse HandleUnexpectedError(
        Exception ex, Stopwatch sw, Activity? thoughtActivity,
        string agentName, string sessionId)
    {
        var failureDurationMs = sw.ElapsedMilliseconds;
        thoughtActivity?.SetTag("agent.duration_ms", failureDurationMs);
        thoughtActivity?.SetTag("error.type", ex.GetType().FullName);

        _metrics?.RecordError("unexpected");

        _logger.LogError(ex,
            "Agent {AgentName} failed after {DurationMs}ms for session {SessionId}",
            agentName, failureDurationMs, sessionId);

        return new ChatResponse(
            "⚠️ Something went wrong while contacting the AI service. Please try again in a moment.",
            sessionId, [], null, failureDurationMs);
    }
}

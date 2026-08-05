using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
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
public partial class AgentExecutionPipeline : IAgentExecutionPipeline
{
    private readonly IChatClient _chatClient;
    private readonly IHubContext<TelemetryHub> _hubContext;
    private readonly IHubContext<StreamingHub>? _streamingHubContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AgentExecutionPipeline> _logger;
    private readonly RetailPulseMetrics? _metrics;
    private readonly StreamingProgressFeature _streamingFeature;

    private static readonly JsonSerializerOptions _caseInsensitiveOptions = new() { PropertyNameCaseInsensitive = true };

    // Matches raw function/tool call leakage patterns in model output:
    // "to=functions.ToolName" prefix (OpenAI-style), optionally followed by garbage text and JSON args
    [GeneratedRegex(@"(?:^|\n)\s*to=functions\.\w+[^\n]*(?:\n|$)", RegexOptions.Multiline)]
    private static partial Regex FunctionCallLeakagePattern();

    // Matches lines with CJK characters (hallucinated/corrupted text) adjacent to JSON or function patterns
    [GeneratedRegex(@"[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]{2,}[^\n]*(?:json|function|tool)[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex CorruptedTextPattern();

    public AgentExecutionPipeline(
        IChatClient chatClient,
        IHubContext<TelemetryHub> hubContext,
        IHubContext<StreamingHub>? streamingHubContext,
        StreamingProgressFeature? streamingFeature,
        IConfiguration configuration,
        ILogger<AgentExecutionPipeline> logger,
        RetailPulseMetrics? metrics = null)
    {
        _chatClient = chatClient;
        _hubContext = hubContext;
        _streamingHubContext = streamingHubContext;
        _streamingFeature = streamingFeature ?? new StreamingProgressFeature();
        _configuration = configuration;
        _logger = logger;
        _metrics = metrics;
    }

    /// <summary>
    /// Simplified constructor for backward compatibility (tests and legacy code).
    /// Streaming progress is disabled when using this constructor.
    /// </summary>
    public AgentExecutionPipeline(
        IChatClient chatClient,
        IHubContext<TelemetryHub> hubContext,
        IConfiguration configuration,
        ILogger<AgentExecutionPipeline> logger,
        RetailPulseMetrics? metrics = null)
        : this(chatClient, hubContext, null, null, configuration, logger, metrics)
    {
    }

    public async Task<ChatResponse> ExecuteAsync(AgentExecutionContext context, CancellationToken ct = default)
    {
        // Delegate to the streaming pipeline when the scoped feature is active
        if (_streamingFeature.IsEnabled)
        {
            return await ExecuteWithProgressAsync(context, ct);
        }

        ChatRequest request = context.Request;
        string sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
        var collector = new TelemetryCollector(_hubContext, sessionId);

        var chatOptions = new ChatOptions
        {
            Temperature = context.Temperature,
            Tools = [.. context.Tools.Select(t => t is AIFunction fn ? new TimedAIFunction(fn) : t)]
        };

        string systemPrompt = BuildSystemPromptWithPrefetch(context.SystemPrompt, context.PrefetchedData);
        List<ChatMessage> messages = BuildMessages(systemPrompt, request);

        var sw = Stopwatch.StartNew();
        using IDisposable toolTimingScope = ToolInvocationTimings.Begin();
        using Activity? thoughtActivity = AgentTelemetry.StartAgentThought(context.AgentName, request.Message);

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

        long thoughtDurationMs = sw.ElapsedMilliseconds;
        thoughtActivity?.SetTag("agent.duration_ms", thoughtDurationMs);

        (int inputTokens, int outputTokens, int totalTokens) = ExtractTokenCounts(response);

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

        long postProcessStart = sw.ElapsedMilliseconds;
        string rawText = response.Text;
        string reply = SanitizeReplyText(string.IsNullOrWhiteSpace(rawText) ? context.FallbackReply : rawText);

        List<ChartSpec> charts = ExtractChartSpecs(response);

        // Recover chart specs the model echoed as raw JSON in its prose (and strip
        // that JSON so it never reaches the chat bubble). Merge distinct recovered
        // charts into the tool-produced set, suppressing only genuine duplicates of
        // charts the tool already produced (a common echo) so a distinct inline
        // chart is not silently dropped.
        InlineChartExtraction inlineCharts = ExtractInlineCharts(reply);
        reply = inlineCharts.Reply;
        charts = MergeInlineCharts(charts, inlineCharts.Charts);

        using Activity? responseActivity = AgentTelemetry.StartAgentResponse(context.AgentName);
        long responseDurationMs = sw.ElapsedMilliseconds - postProcessStart;
        await collector.RecordSpanAsync(
            context.AgentName, "response",
            reply[..Math.Min(200, reply.Length)],
            responseDurationMs);

        long totalDurationMs = sw.ElapsedMilliseconds;

        TokenUsage tokenUsage = BuildTokenUsage(inputTokens, outputTokens, totalTokens, context.ModelName);

        _logger.LogInformation(
            "Agent {AgentName} responded in {DurationMs}ms with {SpanCount} spans, {ChartCount} charts, {TokenCount} tokens",
            context.AgentName, totalDurationMs, collector.Spans.Count, charts.Count, totalTokens);

        _metrics?.RecordAgentExecutionDuration(context.AgentName, totalDurationMs);

        return new ChatResponse(
            reply, sessionId, [.. collector.Spans],
            charts.Count > 0 ? charts : null,
            totalDurationMs, tokenUsage);
    }

    /// <summary>
    /// Augments the system prompt with pre-fetched tool results so the LLM can
    /// synthesize directly without calling those tools — saving one full roundtrip.
    /// </summary>
    internal static string BuildSystemPromptWithPrefetch(string systemPrompt, IReadOnlyDictionary<string, string>? prefetchedData)
    {
        if (prefetchedData is null or { Count: 0 })
            return systemPrompt;

        var sb = new System.Text.StringBuilder(systemPrompt);
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("## Pre-loaded Data");
        sb.AppendLine("The following tool results are already available. Use this data directly — do NOT call these tools again.");

        foreach ((string? toolName, string? result) in prefetchedData)
        {
            sb.AppendLine();
            sb.Append("### ").AppendLine(toolName);
            sb.AppendLine("```json");
            sb.AppendLine(result);
            sb.AppendLine("```");
        }

        return sb.ToString();
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
            List<ChatHistoryMessage> historyMessages = request.History.Count > maxTurns * 2
                ? [.. request.History.Skip(request.History.Count - (maxTurns * 2))]
                : request.History;

            foreach (ChatHistoryMessage historyMessage in historyMessages)
            {
                ChatRole role = string.Equals(historyMessage.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? ChatRole.Assistant
                    : ChatRole.User;
                messages.Add(new ChatMessage(role, historyMessage.Content));
            }
        }

        messages.Add(new(ChatRole.User, request.Message));
        return messages;
    }

    public async Task<ChatResponse> ExecuteWithProgressAsync(AgentExecutionContext context, CancellationToken ct = default)
    {
        ChatRequest request = context.Request;
        string sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
        var collector = new TelemetryCollector(_hubContext, sessionId);

        // Wrap tools with instrumentation for real-time per-tool progress events
        var instrumentedToolMiddleware = new InstrumentedToolMiddleware(_hubContext);
        IReadOnlyList<AITool> instrumentedTools = instrumentedToolMiddleware.WrapTools(context.Tools, sessionId);

        var chatOptions = new ChatOptions
        {
            Temperature = context.Temperature,
            Tools = [.. instrumentedTools]
        };

        string systemPrompt = BuildSystemPromptWithPrefetch(context.SystemPrompt, context.PrefetchedData);
        List<ChatMessage> messages = BuildMessages(systemPrompt, request);

        var sw = Stopwatch.StartNew();
        using IDisposable toolTimingScope = ToolInvocationTimings.Begin();
        using Activity? thoughtActivity = AgentTelemetry.StartAgentThought(context.AgentName, request.Message);

        // Emit progress: thinking phase
        await _hubContext.Clients.Group(sessionId).SendAsync("progress", new
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

        long thoughtDurationMs = sw.ElapsedMilliseconds;
        thoughtActivity?.SetTag("agent.duration_ms", thoughtDurationMs);

        (int inputTokens, int outputTokens, int totalTokens) = ExtractTokenCounts(response);

        await collector.RecordSpanAsync(
            context.AgentName, "thought",
            $"Processing: {request.Message[..Math.Min(100, request.Message.Length)]}",
            thoughtDurationMs,
            inputTokens > 0 ? inputTokens : null,
            outputTokens > 0 ? outputTokens : null);

        await RecordToolSpansAsync(response, collector, thoughtDurationMs, context.OnToolResult, _hubContext, sessionId, ct);

        // Emit progress: synthesizing phase
        await _hubContext.Clients.Group(sessionId).SendAsync("progress", new
        {
            sessionId,
            phase = "synthesizing",
            detail = "Preparing response...",
            timestamp = DateTimeOffset.UtcNow
        }, ct);

        long postProcessStart = sw.ElapsedMilliseconds;
        string rawText = response.Text;
        string reply = SanitizeReplyText(string.IsNullOrWhiteSpace(rawText) ? context.FallbackReply : rawText);

        List<ChartSpec> charts = ExtractChartSpecs(response);

        // Recover chart specs the model echoed as raw JSON in its prose (and strip
        // that JSON so it never reaches the chat bubble or the streamed tokens).
        // Merge distinct recovered charts into the tool-produced set, suppressing
        // only genuine duplicates of tool-produced charts so streaming stays
        // consistent with the non-streaming path.
        InlineChartExtraction inlineCharts = ExtractInlineCharts(reply);
        reply = inlineCharts.Reply;
        charts = MergeInlineCharts(charts, inlineCharts.Charts);

        // Stream the reply token-by-token via StreamingHub for progressive rendering
        await StreamReplyAsync(sessionId, context.AgentName, reply, ct);

        using Activity? responseActivity = AgentTelemetry.StartAgentResponse(context.AgentName);
        long responseDurationMs = sw.ElapsedMilliseconds - postProcessStart;
        await collector.RecordSpanAsync(
            context.AgentName, "response",
            reply[..Math.Min(200, reply.Length)],
            responseDurationMs);

        long totalDurationMs = sw.ElapsedMilliseconds;

        TokenUsage tokenUsage = BuildTokenUsage(inputTokens, outputTokens, totalTokens, context.ModelName);

        _logger.LogInformation(
            "Agent {AgentName} responded (streaming) in {DurationMs}ms with {SpanCount} spans, {ChartCount} charts, {TokenCount} tokens",
            context.AgentName, totalDurationMs, collector.Spans.Count, charts.Count, totalTokens);

        _metrics?.RecordAgentExecutionDuration(context.AgentName, totalDurationMs);

        return new ChatResponse(
            reply, sessionId, [.. collector.Spans],
            charts.Count > 0 ? charts : null,
            totalDurationMs, tokenUsage);
    }

    /// <summary>
    /// Streams the completed reply text token-by-token via the StreamingHub
    /// so the frontend's StreamingMessage component can render progressively.
    /// </summary>
    private async Task StreamReplyAsync(string sessionId, string agentName, string reply, CancellationToken ct)
    {
        if (_streamingHubContext is null) return;

        await StreamingEvents.SendStartAsync(_streamingHubContext, sessionId, agentName);

        // Split on whitespace boundaries and emit word-by-word
        string[] words = reply.Split(' ');
        int tokenIndex = 0;
        foreach (string word in words)
        {
            ct.ThrowIfCancellationRequested();
            string token = tokenIndex == 0 ? word : " " + word;
            await StreamingEvents.SendTokenAsync(_streamingHubContext, sessionId, token, tokenIndex++);
        }

        await StreamingEvents.SendCompleteAsync(_streamingHubContext, sessionId, reply, fromCache: false);
    }

    private static (int input, int output, int total) ExtractTokenCounts(Microsoft.Extensions.AI.ChatResponse response)
    {
        int input = (int)(response.Usage?.InputTokenCount ?? 0);
        int output = (int)(response.Usage?.OutputTokenCount ?? 0);
        int total = (int)(response.Usage?.TotalTokenCount ?? (input + output));
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
        foreach (ChatMessage msg in response.Messages)
        {
            foreach (AIContent content in msg.Contents)
            {
                if (content is FunctionCallContent fc && fc.CallId != null)
                    callIdToName[fc.CallId] = fc.Name;
            }
        }

        // Individual tool durations are captured by the TimedAIFunction / InstrumentedAIFunction
        // wrappers via ToolInvocationTimings (AsyncLocal queue per request). We dequeue here in
        // call order so each tool_call span gets its true wall-clock duration instead of 0.

        foreach (ChatMessage msg in response.Messages)
        {
            foreach (AIContent content in msg.Contents)
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

                    using Activity? toolActivity = AgentTelemetry.StartToolCall(
                        toolCall.Name,
                        JsonSerializer.Serialize(toolCall.Arguments));

                    long toolDurationMs = ToolInvocationTimings.TryDequeue(toolCall.Name);
                    toolActivity?.SetTag("tool.duration_ms", toolDurationMs);

                    await collector.RecordSpanAsync(
                        toolCall.Name, "tool_call",
                        $"Calling {toolCall.Name} with {JsonSerializer.Serialize(toolCall.Arguments)}",
                        toolDurationMs);
                }
                else if (content is FunctionResultContent toolResult)
                {
                    string toolName = callIdToName.GetValueOrDefault(toolResult.CallId ?? "", toolResult.CallId ?? "unknown");
                    string resultText = toolResult.Result?.ToString() ?? "";
                    using Activity? resultActivity = AgentTelemetry.StartToolResult(toolName, resultText.Length);
                    await collector.RecordSpanAsync(
                        toolName, "tool_result",
                        resultText[..Math.Min(200, resultText.Length)],
                        0);

                    if (onToolResult != null)
                        await onToolResult(resultText, ct);
                }
            }
        }
    }

    internal static List<ChartSpec> ExtractChartSpecs(Microsoft.Extensions.AI.ChatResponse chatResponse)
    {
        var charts = new List<ChartSpec>();
        foreach (ChatMessage msg in chatResponse.Messages)
        {
            foreach (AIContent content in msg.Contents)
            {
                if (content is FunctionResultContent toolResult)
                {
                    string? resultText = toolResult.Result?.ToString();
                    if (!string.IsNullOrEmpty(resultText))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(resultText);
                            if (doc.RootElement.TryGetProperty("status", out JsonElement status) &&
                                status.GetString() == "success" &&
                                doc.RootElement.TryGetProperty("chart", out JsonElement chartElement))
                            {
                                ChartSpec? chart = JsonSerializer.Deserialize<ChartSpec>(
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
        }

        return charts;
    }

    internal TokenUsage BuildTokenUsage(int inputTokens, int outputTokens, int totalTokens, string modelName)
    {
        decimal? cost = null;
        IConfigurationSection pricingSection = _configuration.GetSection($"TokenPricing:{modelName}");

        if (pricingSection.Exists())
        {
            decimal inputRate = pricingSection.GetValue<decimal>("InputPerMillion");
            decimal outputRate = pricingSection.GetValue<decimal>("OutputPerMillion");
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

    /// <summary>
    /// Strips raw function/tool call leakage and corrupted text from the model's reply.
    /// Some models occasionally emit partial function call syntax (e.g., "to=functions.ToolName ...")
    /// or hallucinated non-Latin characters as part of the response text. This should never
    /// reach the user.
    /// </summary>
    internal static string SanitizeReplyText(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return reply;

        // Remove "to=functions.*" lines (OpenAI-style function call leakage)
        string sanitized = FunctionCallLeakagePattern().Replace(reply, "");

        // Remove lines with CJK characters adjacent to function/json patterns (corrupted output)
        sanitized = CorruptedTextPattern().Replace(sanitized, "");

        // Trim leading whitespace/newlines that may remain after stripping
        sanitized = sanitized.TrimStart('\n', '\r', ' ');

        // If sanitization removed everything, that means the entire reply was garbage
        return string.IsNullOrWhiteSpace(sanitized)
            ? "I was unable to generate a response. Please try rephrasing your question."
            : sanitized;
    }

    private ChatResponse HandleRateLimitError(
        ClientResultException ex, Stopwatch sw, Activity? thoughtActivity,
        string agentName, string sessionId)
    {
        long failureDurationMs = sw.ElapsedMilliseconds;
        thoughtActivity?.SetTag("agent.duration_ms", failureDurationMs);
        thoughtActivity?.SetTag("error.status_code", ex.Status);

        _metrics?.RecordError("rate_limit");

        _logger.LogWarning(ex,
            "Agent {AgentName} rate-limited after {DurationMs}ms. Status: {Status}",
            agentName, failureDurationMs, ex.Status);

        return new ChatResponse(
            "⏳ The AI service is experiencing high demand. Please wait 30 seconds and try again.",
            sessionId, [], null, failureDurationMs);
    }

    private ChatResponse HandleTimeoutError(
        Exception ex, Stopwatch sw, Activity? thoughtActivity,
        string agentName, string sessionId)
    {
        long failureDurationMs = sw.ElapsedMilliseconds;
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
        long failureDurationMs = sw.ElapsedMilliseconds;
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

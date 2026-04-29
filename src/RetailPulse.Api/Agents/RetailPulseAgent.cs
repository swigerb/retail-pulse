using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Models;
using RetailPulse.Api.Tools;
using RetailPulse.Contracts;
using ChatRequest = RetailPulse.Contracts.ChatRequest;
using ChatResponse = RetailPulse.Contracts.ChatResponse;
using AgentSpan = RetailPulse.Contracts.AgentSpan;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RetailPulse.Api.Agents;

public class RetailPulseAgent
{
    private readonly IChatClient _chatClient;
    private readonly AgentDefinition _agentDef;
    private readonly IHubContext<TelemetryHub> _hubContext;
    private readonly IEnumerable<AITool> _tools;
    private readonly ILogger<RetailPulseAgent> _logger;

    public RetailPulseAgent(
        IChatClient chatClient,
        AgentDefinition agentDef,
        IHubContext<TelemetryHub> hubContext,
        IEnumerable<AITool> tools,
        ILogger<RetailPulseAgent> logger)
    {
        _chatClient = chatClient;
        _agentDef = agentDef;
        _hubContext = hubContext;
        _tools = tools;
        _logger = logger;
    }

    public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
    {
        var sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
        var collector = new TelemetryCollector(_hubContext, sessionId);

        // Build chat options with tools
        var chatOptions = new ChatOptions
        {
            Temperature = (float)_agentDef.Temperature,
            Tools = _tools.ToList()
        };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _agentDef.SystemPrompt),
            new(ChatRole.User, request.Message)
        };

        // Agent thought span — captures the entire GetResponseAsync() call
        // (thinking + tool calls + model response), since MAF handles the
        // tool-calling loop internally and individual timings are unavailable.
        var sw = Stopwatch.StartNew();
        using var thoughtActivity = AgentTelemetry.StartAgentThought(_agentDef.Name, request.Message);

        // Call the model (MAF handles tool calling loop)
        var response = await _chatClient.GetResponseAsync(messages, chatOptions, ct);
        var thoughtDurationMs = sw.ElapsedMilliseconds;
        thoughtActivity?.SetTag("agent.duration_ms", thoughtDurationMs);

        await collector.RecordSpanAsync(
            _agentDef.Name, "thought",
            $"Processing: {request.Message[..Math.Min(100, request.Message.Length)]}",
            thoughtDurationMs);

        // Record tool calls from response (informational sub-spans — durationMs: 0
        // because MAF handles the loop internally and individual timings are lost)
        foreach (var msg in response.Messages)
        {
            if (msg.Role == ChatRole.Assistant)
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
                            0);
                    }
                    else if (content is FunctionResultContent toolResult)
                    {
                        using var resultActivity = AgentTelemetry.StartToolResult(
                            toolResult.CallId ?? "unknown",
                            toolResult.Result?.ToString()?.Length ?? 0);
                        await collector.RecordSpanAsync(
                            toolResult.CallId ?? "unknown", "tool_result",
                            toolResult.Result?.ToString()?[..Math.Min(200, toolResult.Result?.ToString()?.Length ?? 0)] ?? "",
                            0);
                    }
                }
            }
        }

        // Agent response span — only post-processing time (extracting reply, recording)
        var postProcessStart = sw.ElapsedMilliseconds;
        var reply = response.Text ?? "I wasn't able to generate a response.";
        
        // Extract chart specs from tool results
        var charts = ExtractChartSpecs(response);
        
        using var responseActivity = AgentTelemetry.StartAgentResponse(_agentDef.Name);
        var responseDurationMs = sw.ElapsedMilliseconds - postProcessStart;
        await collector.RecordSpanAsync(
            _agentDef.Name, "response",
            reply[..Math.Min(200, reply.Length)],
            responseDurationMs);

        _logger.LogInformation("Agent responded in {DurationMs}ms with {SpanCount} spans and {ChartCount} charts",
            sw.ElapsedMilliseconds, collector.Spans.Count, charts.Count);

        return new ChatResponse(reply, sessionId, collector.Spans.ToList(), charts.Any() ? charts : null);
    }

    private static List<ChartSpec> ExtractChartSpecs(Microsoft.Extensions.AI.ChatResponse chatResponse)
    {
        var charts = new List<ChartSpec>();

        foreach (var msg in chatResponse.Messages)
        {
            if (msg.Role == ChatRole.Assistant)
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
        }

        return charts;
    }

    public static PromptConfiguration LoadPrompts(string yamlPath)
    {
        var yaml = File.ReadAllText(yamlPath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        return deserializer.Deserialize<PromptConfiguration>(yaml);
    }
}

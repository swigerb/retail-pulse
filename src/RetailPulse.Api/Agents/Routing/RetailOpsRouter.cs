using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Agents.Routing;

/// <summary>
/// LLM-based router that classifies user intent and dispatches to the
/// appropriate specialist agent. Uses the same IChatClient pipeline
/// (with OTel + function invocation middleware) for classification.
/// </summary>
public class RetailOpsRouter : IAgentRouter
{
    private readonly IChatClient _chatClient;
    private readonly AgentDefinition _routerDef;
    private readonly IReadOnlyDictionary<string, ISpecialistAgent> _specialists;
    private readonly ILogger<RetailOpsRouter> _logger;

    /// <summary>
    /// Minimum confidence threshold. Below this, the router falls back to
    /// the General agent regardless of classified intent.
    /// </summary>
    private const double ConfidenceThreshold = 0.6;

    public RetailOpsRouter(
        IChatClient chatClient,
        AgentDefinition routerDef,
        IEnumerable<ISpecialistAgent> specialists,
        ILogger<RetailOpsRouter> logger)
    {
        _chatClient = chatClient;
        _routerDef = routerDef;
        _logger = logger;

        // Build a lookup: intent → specialist (first specialist that claims it wins)
        var lookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase);
        foreach (var specialist in specialists)
        {
            foreach (var intent in specialist.SupportedIntents)
            {
                lookup.TryAdd(intent, specialist);
            }
        }
        _specialists = lookup;
    }

    public async Task<RoutingDecision> RouteAsync(
        string message,
        IReadOnlyList<ChatHistoryMessage>? conversationHistory,
        UserContext? user,
        string? tenantId,
        CancellationToken ct = default)
    {
        using var routingActivity = AgentTelemetry.Source.StartActivity(
            "agent.routing", ActivityKind.Internal);
        routingActivity?.SetTag("agent.router", "RetailOpsRouter");
        routingActivity?.SetTag("agent.message_length", message.Length);

        var sw = Stopwatch.StartNew();

        try
        {
            var classification = await ClassifyIntentAsync(message, conversationHistory, ct);

            routingActivity?.SetTag("agent.routing.intent", classification.Intent);
            routingActivity?.SetTag("agent.routing.confidence", classification.Confidence);
            routingActivity?.SetTag("agent.routing.duration_ms", sw.ElapsedMilliseconds);

            // Fall back to general if confidence is below threshold
            if (classification.Confidence < ConfidenceThreshold)
            {
                _logger.LogInformation(
                    "Router confidence {Confidence:F2} below threshold {Threshold:F2} for intent '{Intent}' — falling back to General agent",
                    classification.Confidence, ConfidenceThreshold, classification.Intent);

                routingActivity?.SetTag("agent.routing.fallback", true);
                routingActivity?.SetTag("agent.routing.fallback_reason", "low_confidence");

                return new RoutingDecision(
                    "general", AgentIntent.General, classification.Confidence,
                    classification.DetectedIntents);
            }

            // Look up the specialist for the classified intent
            if (_specialists.TryGetValue(classification.Intent, out var specialist))
            {
                _logger.LogInformation(
                    "Router classified intent '{Intent}' (confidence: {Confidence:F2}) → agent '{AgentKey}'",
                    classification.Intent, classification.Confidence, specialist.Key);

                return new RoutingDecision(
                    specialist.Key, classification.Intent, classification.Confidence,
                    classification.DetectedIntents);
            }

            // Unknown intent — fall back to general
            _logger.LogWarning(
                "No specialist registered for intent '{Intent}' — falling back to General agent",
                classification.Intent);

            routingActivity?.SetTag("agent.routing.fallback", true);
            routingActivity?.SetTag("agent.routing.fallback_reason", "no_specialist");

            return new RoutingDecision(
                "general", AgentIntent.General, classification.Confidence,
                classification.DetectedIntents);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Router classification failed — falling back to General agent");

            routingActivity?.SetTag("agent.routing.fallback", true);
            routingActivity?.SetTag("agent.routing.fallback_reason", "classification_error");
            routingActivity?.SetTag("error.type", ex.GetType().FullName);

            return new RoutingDecision("general", AgentIntent.General, 0.0);
        }
    }

    private async Task<IntentClassification> ClassifyIntentAsync(
        string message,
        IReadOnlyList<ChatHistoryMessage>? conversationHistory,
        CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _routerDef.SystemPrompt)
        };

        // Include recent conversation context for multi-turn awareness
        if (conversationHistory is { Count: > 0 })
        {
            const int maxContextTurns = 4;
            var recentHistory = conversationHistory.Count > maxContextTurns * 2
                ? conversationHistory.Skip(conversationHistory.Count - (maxContextTurns * 2)).ToList()
                : conversationHistory;

            foreach (var turn in recentHistory)
            {
                var role = string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? ChatRole.Assistant
                    : ChatRole.User;
                messages.Add(new ChatMessage(role, turn.Content));
            }
        }

        messages.Add(new(ChatRole.User, message));

        var chatOptions = new ChatOptions
        {
            Temperature = (float)_routerDef.Temperature,
            ResponseFormat = ChatResponseFormat.Json
        };

        var response = await _chatClient.GetResponseAsync(messages, chatOptions, ct);
        var responseText = response.Text ?? "";

        return ParseClassification(responseText, _logger);
    }

    /// <summary>
    /// Parses the LLM's JSON classification response into an IntentClassification.
    /// Expected format: { "intent": "demand/forecasting", "confidence": 0.92, "intents": ["demand/forecasting"] }
    /// </summary>
    internal static IntentClassification ParseClassification(string json, ILogger? logger = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var intent = root.TryGetProperty("intent", out var intentProp)
                ? intentProp.GetString() ?? AgentIntent.General
                : AgentIntent.General;

            var confidence = root.TryGetProperty("confidence", out var confProp)
                ? confProp.GetDouble()
                : 0.5;

            var detectedIntents = new List<string>();
            if (root.TryGetProperty("intents", out var intentsProp) &&
                intentsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in intentsProp.EnumerateArray())
                {
                    var val = item.GetString();
                    if (!string.IsNullOrEmpty(val))
                        detectedIntents.Add(val);
                }
            }

            if (detectedIntents.Count == 0)
                detectedIntents.Add(intent);

            // Normalize: ensure intent is a known value
            if (!AgentIntent.All.Contains(intent))
                intent = AgentIntent.General;

            return new IntentClassification(intent, confidence, detectedIntents);
        }
        catch (JsonException ex)
        {
            logger?.LogDebug(ex, "Failed to parse {Type}", nameof(IntentClassification));
            return new IntentClassification(AgentIntent.General, 0.0, [AgentIntent.General]);
        }
    }

    internal record IntentClassification(
        string Intent,
        double Confidence,
        IReadOnlyList<string> DetectedIntents);
}

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Models;
using RetailPulse.Api.Telemetry;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Agents.Routing;

/// <summary>
/// LLM-based router that classifies user intent and dispatches to the
/// appropriate specialist agent. Uses the same IChatClient pipeline
/// (with OTel + function invocation middleware) for classification.
/// </summary>
public partial class RetailOpsRouter : IAgentRouter
{
    private readonly IChatClient _chatClient;
    private readonly AgentDefinition _routerDef;
    private readonly IReadOnlyDictionary<string, ISpecialistAgent> _specialists;
    private readonly ILogger<RetailOpsRouter> _logger;
    private readonly RetailPulseMetrics? _metrics;

    /// <summary>
    /// Minimum confidence threshold. Below this, the router falls back to
    /// the General agent regardless of classified intent.
    /// </summary>
    private const double _confidenceThreshold = 0.6;

    /// <summary>Confidence assigned to keyword fast-path matches.</summary>
    private const double _keywordMatchConfidence = 0.95;

    /// <summary>
    /// Matches portfolio-level health queries like "how is the portfolio performing" or
    /// "how are all brands performing". Does NOT match single-brand+region queries like
    /// "how is Apex Grill performing in the Southwest" — those are simple data lookups
    /// that should route to GeneralAgent (1 tool call) instead of the Consensus Council (4+ LLM calls).
    /// </summary>
    [GeneratedRegex(@"how is (?:the |our |my )?(portfolio|overall|everything) performing", RegexOptions.IgnoreCase)]
    private static partial Regex PortfolioPerformingRegex();

    /// <summary>
    /// Keyword patterns mapped to their intent. Each entry has "strong" keywords that match
    /// unambiguously on their own, regardless of message length or context. Short or generic
    /// keywords that could fire on ambiguous queries are excluded — the LLM handles those.
    /// </summary>
    private static readonly (string Intent, string[] Keywords)[] _keywordPatterns =
    [
        (AgentIntent.DemandForecasting, ["demand forecast", "sell-through", "velocity forecast"]),
        (AgentIntent.SupplyShipments, ["shipment status", "inventory level", "fulfillment", "stockout", "stock out", "supply chain"]),
        (AgentIntent.CompetitiveMarket, ["pricing pressure", "market share", "price war", "competitor analysis", "competitive landscape"]),
        (AgentIntent.SentimentField, ["field rep", "field feedback", "distributor feedback", "rep feedback", "sentiment analysis"]),
        (AgentIntent.PortfolioHealth, ["portfolio health", "overall health", "brand health", "health council"]),
        (AgentIntent.MarginAnalysis, ["margin analysis", "profitability", "cost structure", "gross margin"]),
        (AgentIntent.Planogram, ["planogram", "shelf space", "shelf placement"]),
        (AgentIntent.StoreOps, ["store operations", "store performance", "retail ops"]),
        (AgentIntent.MemoryManagement, ["remember this", "what do you know about me", "forget about"]),
    ];

    public RetailOpsRouter(
        IChatClient chatClient,
        AgentDefinition routerDef,
        IEnumerable<ISpecialistAgent> specialists,
        ILogger<RetailOpsRouter> logger,
        RetailPulseMetrics? metrics = null)
    {
        _chatClient = chatClient;
        _routerDef = routerDef;
        _logger = logger;
        _metrics = metrics;

        // Build a lookup: intent → specialist (first specialist that claims it wins)
        var lookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase);
        foreach (ISpecialistAgent specialist in specialists)
        {
            foreach (string intent in specialist.SupportedIntents)
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
        using Activity? routingActivity = AgentTelemetry.Source.StartActivity(
            "agent.routing", ActivityKind.Internal);
        routingActivity?.SetTag("agent.router", "RetailOpsRouter");
        routingActivity?.SetTag("agent.message_length", message.Length);

        var sw = Stopwatch.StartNew();

        try
        {
            // Fast-path: skip LLM call if keywords clearly indicate intent
            IntentClassification? keywordResult = TryKeywordClassify(message);
            if (keywordResult is not null)
            {
                _logger.LogInformation("Keyword fast-path matched intent '{Intent}' for message", keywordResult.Intent);
                _metrics?.RecordIntentClassification(keywordResult.Intent, fastPathHit: true);
                _metrics?.RecordRoutingDuration(sw.ElapsedMilliseconds);
                routingActivity?.SetTag("agent.routing.fast_path", true);
                routingActivity?.SetTag("agent.routing.intent", keywordResult.Intent);
                routingActivity?.SetTag("agent.routing.confidence", keywordResult.Confidence);
                routingActivity?.SetTag("agent.routing.duration_ms", sw.ElapsedMilliseconds);

                if (_specialists.TryGetValue(keywordResult.Intent, out ISpecialistAgent? fastPathSpecialist))
                {
                    return new RoutingDecision(
                        fastPathSpecialist.Key, keywordResult.Intent, keywordResult.Confidence,
                        keywordResult.DetectedIntents);
                }

                // Keyword matched but no specialist registered — fall through to LLM
            }

            IntentClassification classification = await ClassifyIntentAsync(message, conversationHistory, ct);

            _metrics?.RecordIntentClassification(classification.Intent, fastPathHit: false);
            _metrics?.RecordRoutingDuration(sw.ElapsedMilliseconds);
            routingActivity?.SetTag("agent.routing.intent", classification.Intent);
            routingActivity?.SetTag("agent.routing.confidence", classification.Confidence);
            routingActivity?.SetTag("agent.routing.duration_ms", sw.ElapsedMilliseconds);

            // Fall back to general if confidence is below threshold
            if (classification.Confidence < _confidenceThreshold)
            {
                _logger.LogInformation(
                    "Router confidence {Confidence:F2} below threshold {Threshold:F2} for intent '{Intent}' — falling back to General agent",
                    classification.Confidence, _confidenceThreshold, classification.Intent);

                routingActivity?.SetTag("agent.routing.fallback", true);
                routingActivity?.SetTag("agent.routing.fallback_reason", "low_confidence");

                return new RoutingDecision(
                    "general", AgentIntent.General, classification.Confidence,
                    classification.DetectedIntents);
            }

            // Look up the specialist for the classified intent
            if (_specialists.TryGetValue(classification.Intent, out ISpecialistAgent? specialist))
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

    /// <summary>
    /// Attempts to classify intent using simple keyword matching.
    /// Returns null if no confident match is found, allowing fallback to LLM classification.
    /// </summary>
    private static IntentClassification? TryKeywordClassify(string message)
    {
        // Portfolio-level "performing" queries → council (multi-agent synthesis)
        // Single-brand queries like "How is Apex Grill performing in the Southwest?"
        // intentionally fall through to LLM classification → routes to GeneralAgent
        // (1 tool call) instead of the Consensus Council (4+ LLM roundtrips).
        if (PortfolioPerformingRegex().IsMatch(message))
        {
            return new IntentClassification(
                AgentIntent.PortfolioHealth, _keywordMatchConfidence, [AgentIntent.PortfolioHealth]);
        }

        foreach ((string? intent, string[]? keywords) in _keywordPatterns)
        {
            foreach (string keyword in keywords)
            {
                if (message.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return new IntentClassification(intent, _keywordMatchConfidence, [intent]);
                }
            }
        }

        return null;
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
            IReadOnlyList<ChatHistoryMessage> recentHistory = conversationHistory.Count > maxContextTurns * 2
                ? [.. conversationHistory.Skip(conversationHistory.Count - (maxContextTurns * 2))]
                : conversationHistory;

            foreach (ChatHistoryMessage turn in recentHistory)
            {
                ChatRole role = string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase)
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

        Microsoft.Extensions.AI.ChatResponse response = await _chatClient.GetResponseAsync(messages, chatOptions, ct);
        string responseText = response.Text ?? "";

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
            JsonElement root = doc.RootElement;

            string intent = root.TryGetProperty("intent", out JsonElement intentProp)
                ? intentProp.GetString() ?? AgentIntent.General
                : AgentIntent.General;

            double confidence = root.TryGetProperty("confidence", out JsonElement confProp)
                ? confProp.GetDouble()
                : 0.5;

            var detectedIntents = new List<string>();
            if (root.TryGetProperty("intents", out JsonElement intentsProp) &&
                intentsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in intentsProp.EnumerateArray())
                {
                    string? val = item.GetString();
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

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Caching;
using RetailPulse.Api.Charts;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Models;
using RetailPulse.Api.Telemetry;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Agents.Routing;

/// <summary>
/// LLM-based router that classifies user intent and dispatches to the
/// appropriate specialist agent. Uses a dedicated IChatClient for classification
/// (optionally a lighter model) and caches results to reduce TPM consumption.
/// </summary>
public partial class RetailOpsRouter : IAgentRouter
{
    private readonly IChatClient _chatClient;
    private readonly AgentDefinition _routerDef;
    private readonly IReadOnlyDictionary<string, ISpecialistAgent> _specialists;
    private readonly ILogger<RetailOpsRouter> _logger;
    private readonly RetailPulseMetrics? _metrics;
    private readonly RouterClassificationCache? _classificationCache;
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>
    /// MAF agent name used when the router classifies intent through
    /// <see cref="MafAgentInvoker"/>. Kept as a stable, discoverable constant so
    /// characterization tests can assert the router routes through the shared MAF
    /// invocation path (not directly through <see cref="IChatClient"/>).
    /// </summary>
    internal const string MafAgentName = "RetailOpsRouter.classifier";

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
    /// Matches single-brand performance queries like "how is Apex Grill performing in the Southwest?"
    /// or "how is Brand X doing this quarter?" — these are simple data lookups that route to GeneralAgent.
    /// Excludes portfolio-level queries (matched separately by <see cref="PortfolioPerformingRegex"/>).
    /// </summary>
    [GeneratedRegex(@"how is .+ (performing|doing)", RegexOptions.IgnoreCase)]
    private static partial Regex BrandPerformingRegex();

    /// <summary>
    /// Matches single-brand depletion lookup prompts like "Show me Pinnacle Hardware depletion stats in the Midwest for Q1"
    /// and keeps them on the lightweight GeneralAgent path (single MCP call) instead of the slower forecasting workflow.
    /// </summary>
    [GeneratedRegex(@"(?:show me|give me|what(?:'s| is| are)?|how(?:'s| is| are)?) .+ depletion stats", RegexOptions.IgnoreCase)]
    private static partial Regex BrandDepletionStatsRegex();

    /// <summary>
    /// Matches simple single-brand depletion trend lookups like "How are FreshMart depletions trending in the Northeast?"
    /// so they use the factual depletion path instead of forecast/seasonality/risk tool chains.
    /// </summary>
    [GeneratedRegex(@"(?:how are|show me|give me) .+ depletion trends?|how are .+ depletions trending", RegexOptions.IgnoreCase)]
    private static partial Regex BrandDepletionTrendRegex();

    /// <summary>
    /// Matches cross-region depletion comparison queries like "Compare depletion trends across all regions for this quarter".
    /// These route directly to DemandForecasting (skipping the LLM router call) to save one AI roundtrip.
    /// </summary>
    [GeneratedRegex(@"compare\b.*depletion.*(?:region|all region|across)", RegexOptions.IgnoreCase)]
    private static partial Regex CrossRegionDepletionCompareRegex();

    private static readonly string[] _complexDemandIndicators =
    [
        "compare",
        "forecast",
        "predict",
        "seasonal",
        "seasonality",
        "risk",
        "risks",
        "portfolio",
        "all brands"
    ];

    /// <summary>
    /// Intent → keyword mapping consumed by the fast-path classifier. Populated from
    /// configuration (each specialist's <c>AgentDefinition.KeywordFastPaths</c> plus
    /// any orchestration intents supplied via <see cref="RouterIntentConfig"/>).
    /// Data-driven per ADR-008 — no hardcoded keyword table remains here.
    /// </summary>
    public IReadOnlyList<(string Intent, string[] Keywords)> KeywordPatterns { get; }

    /// <summary>Intents this router knows about — union of every specialist and orchestrator entry.</summary>
    public IReadOnlyCollection<string> KnownIntents { get; }

    public RetailOpsRouter(
        IChatClient chatClient,
        AgentDefinition routerDef,
        IEnumerable<ISpecialistAgent> specialists,
        ILogger<RetailOpsRouter> logger,
        IReadOnlyList<RouterIntentConfig>? intentConfigs = null,
        RetailPulseMetrics? metrics = null,
        RouterClassificationCache? classificationCache = null,
        ILoggerFactory? loggerFactory = null)
    {
        _chatClient = chatClient;
        _routerDef = routerDef;
        _logger = logger;
        _metrics = metrics;
        _classificationCache = classificationCache;
        _loggerFactory = loggerFactory;

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

        // Build keyword fast-path table from configuration. Specialists contribute
        // their KeywordFastPaths via ISpecialistAgent.KeywordFastPaths (populated
        // from AgentDefinition), and orchestration intents (e.g., council/health)
        // arrive as RouterIntentConfig entries because they have no specialist.
        var patterns = new List<(string Intent, string[] Keywords)>();
        var seenIntents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ISpecialistAgent specialist in lookup.Values.Distinct())
        {
            // Defensive against mocks and legacy impls that don't override the
            // default interface member — treat null as empty.
            IReadOnlyList<string> keywords = specialist.KeywordFastPaths ?? [];
            if (keywords.Count == 0) continue;

            string intent = specialist.SupportedIntents.Count > 0
                ? specialist.SupportedIntents[0]
                : AgentIntent.General;
            patterns.Add((intent, [.. keywords]));
            seenIntents.Add(intent);
        }

        if (intentConfigs is not null)
        {
            foreach (RouterIntentConfig cfg in intentConfigs)
            {
                if (cfg.KeywordFastPaths.Count == 0) continue;
                patterns.Add((cfg.Intent, [.. cfg.KeywordFastPaths]));
                seenIntents.Add(cfg.Intent);
            }
        }

        KeywordPatterns = patterns;
        KnownIntents = seenIntents;
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

            // Check classification cache before making an LLM call
            if (_classificationCache is not null)
            {
                RouterCacheEntry? cached = _classificationCache.TryGet(message);
                if (cached is not null)
                {
                    _logger.LogInformation("Router cache hit for intent '{Intent}' (confidence: {Confidence:F2})", cached.Intent, cached.Confidence);
                    _metrics?.RecordIntentClassification(cached.Intent, fastPathHit: true);
                    _metrics?.RecordRoutingDuration(sw.ElapsedMilliseconds);
                    routingActivity?.SetTag("agent.routing.cache_hit", true);
                    routingActivity?.SetTag("agent.routing.intent", cached.Intent);
                    routingActivity?.SetTag("agent.routing.confidence", cached.Confidence);
                    routingActivity?.SetTag("agent.routing.duration_ms", sw.ElapsedMilliseconds);

                    return _specialists.TryGetValue(cached.Intent, out ISpecialistAgent? cachedSpecialist)
                        ? new RoutingDecision(
                            cachedSpecialist.Key, cached.Intent, cached.Confidence, cached.DetectedIntents)
                        : new RoutingDecision(
                        "general", AgentIntent.General, cached.Confidence, cached.DetectedIntents);
                }
            }

            IntentClassification classification = await ClassifyIntentAsync(message, conversationHistory, ct);

            // Cache the LLM classification result for future identical/similar queries
            _classificationCache?.Set(message, classification.Intent, classification.Confidence, classification.DetectedIntents);

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
    /// <remarks>
    /// Instance method now — the keyword patterns are configuration-driven and live on the
    /// router instance (issue #98). Structural fast-paths (memory commands, chart intent,
    /// portfolio-performing regex, single-brand-lookup regex) remain hardcoded because they
    /// encode routing behavior, not per-agent taxonomy.
    /// </remarks>
    private IntentClassification? TryKeywordClassify(string message)
    {
        // Memory commands have absolute priority — must not be intercepted by
        // brand-lookup shortcuts or portfolio regex patterns.
        if (IsMemoryCommand(message))
        {
            return new IntentClassification(
                AgentIntent.MemoryManagement, _keywordMatchConfidence, [AgentIntent.MemoryManagement]);
        }

        // Explicit visualization requests take priority over broad keyword/LLM routing.
        // A prompt like "Show a gauge chart for <brand> inventory health in the Midwest"
        // must reach a data + CreateChart specialist (here: supply/shipments via the
        // "inventory" cue) rather than being classified as council/health on the bare
        // keyword "health". The detector is generic (chart-type word + domain cue) and
        // never yields the portfolio-health council, which produces prose/votes, no chart.
        ChartIntent chartIntent = ChartRequestDetector.Detect(message);
        if (chartIntent.IsExplicitChartRequest)
        {
            return new IntentClassification(
                chartIntent.RoutedIntent, _keywordMatchConfidence, [chartIntent.RoutedIntent]);
        }

        // Portfolio-level "performing" queries → council (multi-agent synthesis)
        // Single-brand queries intentionally fall through to the lightweight GeneralAgent
        // path (1 MCP call) instead of the slower Demand Forecast agent workflow.
        if (PortfolioPerformingRegex().IsMatch(message))
        {
            return new IntentClassification(
                AgentIntent.PortfolioHealth, _keywordMatchConfidence, [AgentIntent.PortfolioHealth]);
        }

        // Cross-region depletion comparisons: route directly to DemandForecasting,
        // bypassing the LLM router call to save one AI roundtrip and reduce token
        // consumption on queries that will already be token-heavy (multi-region data).
        if (CrossRegionDepletionCompareRegex().IsMatch(message))
        {
            return new IntentClassification(
                AgentIntent.DemandForecasting, _keywordMatchConfidence, [AgentIntent.DemandForecasting]);
        }

        // Simple single-brand performance/depletion lookups should bypass the LLM router
        // and skip the forecast tool chain entirely.
        if (IsSimpleSingleBrandLookup(message))
        {
            return new IntentClassification(
                AgentIntent.General, _keywordMatchConfidence, [AgentIntent.General]);
        }

        foreach ((string? intent, string[]? keywords) in KeywordPatterns)
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

    private static bool IsMemoryCommand(string message)
    {
        string lower = message.ToLowerInvariant();

        // Store intents
        if (lower.StartsWith("remember that") || lower.StartsWith("remember this") || lower.StartsWith("remember "))
            return true;

        // Destructive intents
        return lower.Contains("forget") || lower.Contains("clear my") || lower.Contains("start fresh")
            || lower.Contains("reset my context") || lower.Contains("what do you know about me");
    }

    private static bool IsSimpleSingleBrandLookup(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        string lower = $" {message.ToLowerInvariant()} ";
        return !_complexDemandIndicators.Any(lower.Contains)
            && (BrandPerformingRegex().IsMatch(message)
                || BrandDepletionStatsRegex().IsMatch(message)
                || BrandDepletionTrendRegex().IsMatch(message));
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

        // Route classification through a real MAF ChatClientAgent so the intent-classifier
        // is a genuine MAF agent primitive, not a bare IChatClient call. UseProvidedChatClientAsIs
        // (inside MafAgentInvoker) preserves the DI-wired FunctionInvokingChatClient +
        // OpenTelemetry decorators unchanged — the router carries no tools, so no tool
        // execution is triggered by wrapping the classifier in an agent primitive.
        AgentResponse mafResponse = await MafAgentInvoker.RunAsync(
            _chatClient,
            MafAgentName,
            messages,
            chatOptions,
            _loggerFactory,
            ct);
        string responseText = mafResponse.Text ?? "";

        return ParseClassification(responseText, _logger, KnownIntents);
    }

    /// <summary>
    /// Parses the LLM's JSON classification response into an IntentClassification.
    /// Expected format: { "intent": "demand/forecasting", "confidence": 0.92, "intents": ["demand/forecasting"] }
    /// </summary>
    internal static IntentClassification ParseClassification(
        string json,
        ILogger? logger = null,
        IReadOnlyCollection<string>? knownIntents = null)
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

            // Normalize: ensure intent is a known value. The typed AgentIntent enum is
            // the baseline; the router also injects its per-instance KnownIntents so
            // purely-configured specialists (added via prompts.yaml, no C# change) route
            // correctly under issue #98.
            bool isKnown = AgentIntent.All.Contains(intent)
                || (knownIntents is not null &&
                    knownIntents.Contains(intent, StringComparer.OrdinalIgnoreCase));
            if (!isKnown)
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

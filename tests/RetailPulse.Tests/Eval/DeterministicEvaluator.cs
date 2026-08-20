using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Agents.Routing;
using RetailPulse.Api.Charts;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Tests.Eval;

/// <summary>
/// Deterministic scorer for a golden case. Grades:
/// <list type="bullet">
/// <item>explicit-chart detection and canonical chart type via <see cref="ChartRequestDetector.Detect(string)"/>.</item>
/// <item>router routing intent via the real <see cref="RetailOpsRouter"/>, using a tracked
/// stub <see cref="IChatClient"/>. Cases marked <c>routing_mode = keyword-fast-path</c> assert
/// the LLM was <b>not</b> called. Cases marked <c>routing_mode = llm-required</c> assert the
/// LLM <b>was</b> called (a fallthrough regression is caught).</item>
/// <item>memory-command detection via the router (a keyword hit with intent
/// <c>memory/management</c> at confidence 0.95).</item>
/// </list>
/// Every rule is pure code — no live model — so results are reproducible byte-for-byte
/// on any host. Model-graded rubric properties (refusal wording, clarification quality,
/// retrieval fidelity) are not evaluated here; they are surfaced separately in the report
/// as informational and never gate CI on their own.
/// </summary>
public sealed class DeterministicEvaluator
{
    /// <summary>Evaluate one case and return a per-property result envelope.</summary>
    public CaseResult Evaluate(GoldenCase c)
    {
        ArgumentNullException.ThrowIfNull(c);
        GoldenExpectations exp = c.Expectations;

        // Chart properties: pure regex, always deterministic.
        ChartIntent chartIntent = ChartRequestDetector.Detect(c.Prompt);

        var explicitChart = new PropertyResult<bool>(
            Expected: exp.ExplicitChart,
            Observed: chartIntent.IsExplicitChartRequest,
            Pass: chartIntent.IsExplicitChartRequest == exp.ExplicitChart);

        var chartType = new PropertyResult<string?>(
            Expected: exp.ChartType,
            Observed: chartIntent.ChartType,
            Pass: chartIntent.IsExplicitChartRequest
                ? string.Equals(chartIntent.ChartType, exp.ChartType, StringComparison.Ordinal)
                : chartIntent.ChartType is null && exp.ChartType is null);

        // Routing: run the actual router with a call-tracking stub IChatClient.
        (string? routingIntent, bool llmCalled, RoutingDecision routingDecision) = ObserveRouting(c.Prompt);

        PropertyResult<string?> routingIntentResult;
        PropertyResult<bool> llmCallResult;

        if (string.Equals(exp.RoutingMode, "keyword-fast-path", StringComparison.Ordinal))
        {
            routingIntentResult = new PropertyResult<string?>(
                Expected: exp.RoutingIntent,
                Observed: routingIntent,
                Pass: string.Equals(routingIntent, exp.RoutingIntent, StringComparison.Ordinal));

            llmCallResult = new PropertyResult<bool>(
                Expected: false,
                Observed: llmCalled,
                Pass: !llmCalled);
        }
        else if (string.Equals(exp.RoutingMode, "llm-required", StringComparison.Ordinal))
        {
            // Routing intent is model-dependent — not graded deterministically. Record the
            // observed value for baseline diffing and mark the property as "not graded".
            routingIntentResult = new PropertyResult<string?>(
                Expected: null,
                Observed: routingIntent,
                Pass: true,
                Notes: "routing intent depends on the live LLM classifier — recorded, not gated");

            llmCallResult = new PropertyResult<bool>(
                Expected: true,
                Observed: llmCalled,
                Pass: llmCalled);
        }
        else
        {
            throw new InvalidOperationException(
                $"Case '{c.Id}' has unrecognized routing_mode '{exp.RoutingMode}'. " +
                "Expected 'keyword-fast-path' or 'llm-required'.");
        }

        // Memory-command detection = router assigned memory/management on the keyword path.
        bool observedMemory =
            !llmCalled
            && string.Equals(routingDecision.Intent, AgentIntent.MemoryManagement, StringComparison.Ordinal)
            && routingDecision.Confidence >= 0.94;

        var memoryResult = new PropertyResult<bool>(
            Expected: exp.MemoryCommand,
            Observed: observedMemory,
            Pass: observedMemory == exp.MemoryCommand);

        bool allPass =
            explicitChart.Pass
            && chartType.Pass
            && routingIntentResult.Pass
            && llmCallResult.Pass
            && memoryResult.Pass;

        bool deterministicallyGraded =
            string.Equals(exp.RoutingMode, "keyword-fast-path", StringComparison.Ordinal);

        // Rough token estimate: 4 chars per token is the standard approximation. Recorded
        // so the offline harness has a nonzero prompt-token budget footprint for cost
        // tracking, and so a future live run can compare against this floor.
        int estimatedTokens = Math.Max(1, c.Prompt.Length / 4);

        return new CaseResult
        {
            Id = c.Id,
            Category = c.Category,
            Prompt = c.Prompt,
            ExplicitChart = explicitChart,
            ChartType = chartType,
            RoutingIntent = routingIntentResult,
            LlmCallMade = llmCallResult,
            MemoryCommand = memoryResult,
            DeterministicallyGraded = deterministicallyGraded,
            AllPropertiesPassed = allPass,
            PromptCharacters = c.Prompt.Length,
            EstimatedPromptTokens = estimatedTokens,
        };
    }

    /// <summary>
    /// Invoke the real <see cref="RetailOpsRouter"/> against <paramref name="prompt"/> with a
    /// call-counting stub <see cref="IChatClient"/> so we can observe both the routed intent
    /// and whether the LLM path was reached.
    /// </summary>
    private static (string intent, bool llmCalled, RoutingDecision decision) ObserveRouting(string prompt)
    {
        int callCount = 0;
        var stubClient = new Mock<IChatClient>();
        stubClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => callCount++)
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant,
                    // Above-threshold general classification: routing must not gate on this
                    // for llm-required cases — the observed value is informational only.
                    $"{{\"intent\":\"{AgentIntent.General}\",\"confidence\":0.8}}")));

        RetailOpsRouter router = BuildRouter(stubClient.Object);
        RoutingDecision decision = router.RouteAsync(prompt, null, null, null)
            .GetAwaiter().GetResult();

        return (decision.Intent, callCount > 0, decision);
    }

    /// <summary>
    /// Construct a <see cref="RetailOpsRouter"/> with one mock specialist per known intent so
    /// that any keyword-fast-path or LLM classification result can be dispatched. Specialist
    /// implementations are not exercised (this evaluator grades routing only).
    /// </summary>
    private static RetailOpsRouter BuildRouter(IChatClient chatClient)
    {
        var specialists = new List<ISpecialistAgent>();
        foreach (string intent in AgentIntent.All)
        {
            var mock = new Mock<ISpecialistAgent>();
            mock.Setup(s => s.Key).Returns(SpecialistKeyForIntent(intent));
            mock.Setup(s => s.DisplayName).Returns($"Eval Stub ({intent})");
            mock.Setup(s => s.SupportedIntents).Returns([intent]);
            mock.Setup(s => s.KeywordFastPaths).Returns(KeywordsForIntent(intent));
            specialists.Add(mock.Object);
        }

        var routerDef = new AgentDefinition
        {
            Name = "Router",
            Model = "eval-stub-router",
            SystemPrompt = "Classify user intent into retail categories. Return JSON.",
            Temperature = 0.0,
        };

        // Mirror production orchestration intents (council/health, scorecard/portfolio)
        // so keyword-fast-path golden cases route to them the same way the live API would.
        var orchestrationIntents = new List<RouterIntentConfig>
        {
            new(AgentIntent.PortfolioHealth,
                ["portfolio health", "overall health", "brand health", "health council"]),
            new(AgentIntent.Scorecard,
                ["scorecard", "brand scorecard", "performance scorecard"]),
        };

        return new RetailOpsRouter(
            chatClient,
            routerDef,
            specialists,
            NullLogger<RetailOpsRouter>.Instance,
            intentConfigs: orchestrationIntents);
    }

    private static string SpecialistKeyForIntent(string intent) => intent switch
    {
        AgentIntent.General => "general",
        AgentIntent.DemandForecasting => "demand-forecasting",
        AgentIntent.PromotionTrade => "promotion-trade",
        AgentIntent.SupplyShipments => "supply-shipments",
        AgentIntent.CompetitiveMarket => "competitive-market",
        AgentIntent.SentimentField => "sentiment-field",
        AgentIntent.MemoryManagement => "memory-management",
        AgentIntent.PortfolioHealth => "council",
        AgentIntent.StoreOps => "store-ops",
        AgentIntent.Planogram => "planogram",
        AgentIntent.MarginAnalysis => "margin",
        AgentIntent.Scorecard => "scorecard",
        _ => "general",
    };

    /// <summary>
    /// Mirrors the production keyword fast-paths declared in <c>prompts.yaml</c> per intent.
    /// The evaluator has to seed these directly onto the stub specialists so that the router
    /// can build the same keyword table it would in the live pipeline — without pulling the
    /// full YAML loader into the eval harness.
    /// </summary>
    private static IReadOnlyList<string> KeywordsForIntent(string intent) => intent switch
    {
        AgentIntent.DemandForecasting =>
            ["demand forecast", "sell-through", "velocity forecast"],
        AgentIntent.PromotionTrade =>
            ["promotion", "trade spend", "promo effectiveness", "promotion roi"],
        AgentIntent.SentimentField =>
            ["field rep", "field feedback", "distributor feedback", "rep feedback", "sentiment analysis"],
        AgentIntent.CompetitiveMarket =>
            ["pricing pressure", "market share", "price war", "competitor analysis", "competitive landscape"],
        AgentIntent.SupplyShipments =>
            ["shipment status", "inventory level", "fulfillment", "stockout", "stock out", "supply chain"],
        AgentIntent.StoreOps =>
            ["store operations", "store performance", "retail ops"],
        AgentIntent.Planogram =>
            ["planogram", "shelf space", "shelf placement"],
        AgentIntent.MarginAnalysis =>
            ["margin analysis", "profitability", "cost structure", "gross margin"],
        AgentIntent.MemoryManagement =>
            [
                "remember that", "remember this", "forget", "clear my", "clear my history",
                "clear my data", "start fresh", "reset my context", "forget what I told you",
                "what do you know about me"
            ],
        _ => [],
    };
}

/// <summary>Per-property scoring outcome. <typeparamref name="T"/> is the observed value type.</summary>
public sealed record PropertyResult<T>(
    [property: JsonPropertyName("expected")] T? Expected,
    [property: JsonPropertyName("observed")] T? Observed,
    [property: JsonPropertyName("pass")] bool Pass,
    [property: JsonPropertyName("notes")] string? Notes = null);

/// <summary>Per-case scored envelope. Serialized into the report and the baseline file.</summary>
public sealed record CaseResult
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("category")] public string Category { get; init; } = "";
    [JsonPropertyName("prompt")] public string Prompt { get; init; } = "";
    [JsonPropertyName("explicit_chart")] public PropertyResult<bool> ExplicitChart { get; init; } = new(false, false, true);
    [JsonPropertyName("chart_type")] public PropertyResult<string?> ChartType { get; init; } = new(null, null, true);
    [JsonPropertyName("routing_intent")] public PropertyResult<string?> RoutingIntent { get; init; } = new(null, null, true);
    [JsonPropertyName("llm_call_made")] public PropertyResult<bool> LlmCallMade { get; init; } = new(false, false, true);
    [JsonPropertyName("memory_command")] public PropertyResult<bool> MemoryCommand { get; init; } = new(false, false, true);
    [JsonPropertyName("deterministically_graded")] public bool DeterministicallyGraded { get; init; }
    [JsonPropertyName("all_properties_passed")] public bool AllPropertiesPassed { get; init; }
    [JsonPropertyName("prompt_characters")] public int PromptCharacters { get; init; }
    [JsonPropertyName("estimated_prompt_tokens")] public int EstimatedPromptTokens { get; init; }
}

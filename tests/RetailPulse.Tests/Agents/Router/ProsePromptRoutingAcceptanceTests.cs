using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Agents.Routing;
using RetailPulse.Api.Charts;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Prompts;
using RetailPulse.Contracts.Routing;
using Xunit;

namespace RetailPulse.Tests.Agents.Router;

/// <summary>
/// End-to-end routing acceptance for every curated PROSE prompt (issue #63).
///
/// The chart-only acceptance matrix in <see cref="ChartAcceptanceMatrixTests"/>
/// already guards the render invariant per curated chart prompt. This suite closes
/// the routing invariant for every remaining curated prompt — the prose entries in
/// General, Grocery, QSR, Home Improvement, Office Supply, and Furniture — by
/// asserting, deterministically and offline, that each prose prompt:
///
/// <list type="bullet">
///   <item>Routes through <see cref="RetailOpsRouter"/> to the specialist declared
///     in <see cref="ProsePromptAcceptanceManifest"/> — never to the multi-agent
///     council (<see cref="AgentIntent.PortfolioHealth"/>), which would produce a
///     slow, prose-only response with no tool loop.</item>
///   <item>Is NOT classified by <see cref="ChartRequestDetector"/> as an explicit
///     chart request — this guarantees the routing layer will not stream the prompt
///     through the chart-fulfillment pipeline, which is the mechanism by which a
///     prose response could accidentally leak chart JSON.</item>
///   <item>Resolves to a specialist agent key that is declared in
///     <c>prompts.yaml</c> with a non-empty tool list, so a live invocation will
///     always have at least one tool to call and cannot produce an empty prose reply
///     for lack of a tool.</item>
/// </list>
///
/// The tool-list assertion is loaded once from <c>prompts.yaml</c>; the router
/// setup mirrors <see cref="RetailOpsRouterTests"/> so the same fast-path patterns,
/// specialist wiring, and orchestration intents production uses are exercised.
/// </summary>
public sealed class ProsePromptRoutingAcceptanceTests
{
    public static IEnumerable<object[]> AllCases()
        => ProsePromptAcceptanceManifest.Cases.Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(AllCases))]
    public async Task Case_RoutesToExpectedSpecialist_AndNeverToCouncil(ProsePromptAcceptanceCase c)
    {
        RetailOpsRouter router = CreateRouter(mockedLlmIntent: c.ExpectedIntent);

        RoutingDecision result = await router.RouteAsync(c.Prompt, null, null, null);

        result.Intent.Should().Be(c.ExpectedIntent,
            $"prose prompt '{c.Prompt}' must classify to '{c.ExpectedIntent}' ({c.Rationale})");
        result.AgentKey.Should().Be(c.ExpectedAgentKey,
            $"prose prompt '{c.Prompt}' must be handled by the '{c.ExpectedAgentKey}' specialist");

        result.Intent.Should().NotBe(
            AgentIntent.PortfolioHealth,
            $"prose prompt '{c.Prompt}' must never route to the multi-agent council");
        result.AgentKey.Should().NotBe(
            "council",
            $"prose prompt '{c.Prompt}' must never target the council agent key");
    }

    [Theory]
    [MemberData(nameof(AllCases))]
    public void Case_IsNotClassifiedAsExplicitChartRequest(ProsePromptAcceptanceCase c)
    {
        // The router's chart fast-path (via ChartRequestDetector) will hijack any
        // prompt classified as an explicit chart request and steer it into the chart
        // fulfillment pipeline. If a PROSE curated prompt matches, the fulfillment
        // pipeline will try to render a chart the user did not ask for — the exact
        // "chart JSON leaked into a prose answer" symptom this manifest prevents.
        ChartIntent intent = ChartRequestDetector.Detect(c.Prompt);

        intent.IsExplicitChartRequest.Should().BeFalse(
            $"prose curated prompt '{c.Prompt}' must NOT be classified as an explicit chart "
            + "request — otherwise the chart-fulfillment path is forced onto a prose prompt");
        intent.ChartType.Should().BeNull(
            $"prose prompt '{c.Prompt}' must not carry a chart-type hint");
    }

    [Theory]
    [MemberData(nameof(AllCases))]
    public void Case_ExpectedAgentKey_ResolvesToASpecialistWithAtLeastOneTool(ProsePromptAcceptanceCase c)
    {
        IReadOnlyDictionary<string, int> toolCounts = SpecialistToolCountsFromPromptsYaml.Value;

        toolCounts.Should().ContainKey(c.ExpectedAgentKey,
            $"prompts.yaml must declare a specialist for key '{c.ExpectedAgentKey}' required by "
            + $"prose prompt '{c.Prompt}'");

        toolCounts[c.ExpectedAgentKey].Should().BeGreaterThanOrEqualTo(
            1,
            $"specialist '{c.ExpectedAgentKey}' must declare at least one tool in prompts.yaml so "
            + $"prose prompt '{c.Prompt}' has at least one tool to invoke during fulfillment");
    }

    // ─── prompts.yaml tool-count loader ───────────────────────────────────

    /// <summary>
    /// Live tool counts per specialist agent key, parsed from <c>prompts.yaml</c>
    /// once per test run. Loaded lazily so the fixture cost is paid only if the
    /// tool-count theory actually runs.
    /// </summary>
    private static readonly Lazy<IReadOnlyDictionary<string, int>> SpecialistToolCountsFromPromptsYaml =
        new(() =>
        {
            string yamlPath = ResolveRepoRelativePath("src", "RetailPulse.Api", "prompts.yaml");
            string[] lines = File.ReadAllLines(yamlPath);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            string? currentKey = null;
            bool inToolsBlock = false;
            int toolBlockIndent = -1;
            int count = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();

                // Detect: `    key: "some-key"` — capture the current specialist key.
                if (trimmed.StartsWith("key:", StringComparison.Ordinal))
                {
                    if (currentKey is not null)
                    {
                        counts[currentKey] = inToolsBlock ? count : counts.GetValueOrDefault(currentKey, 0);
                    }

                    string valuePart = trimmed[4..].Trim().Trim('"', '\'');
                    currentKey = valuePart;
                    inToolsBlock = false;
                    toolBlockIndent = -1;
                    count = 0;
                    counts[currentKey] = 0;
                    continue;
                }

                if (currentKey is null) continue;

                // Detect: `    tools: []` — an empty tool list block.
                if (trimmed.StartsWith("tools:", StringComparison.Ordinal))
                {
                    string tail = trimmed[6..].Trim();
                    if (tail == "[]")
                    {
                        counts[currentKey] = 0;
                        inToolsBlock = false;
                        continue;
                    }
                    inToolsBlock = true;
                    toolBlockIndent = line.Length - trimmed.Length;
                    count = 0;
                    continue;
                }

                if (!inToolsBlock) continue;

                // Count list items indented deeper than the `tools:` key.
                int indent = line.Length - trimmed.Length;
                if (trimmed.StartsWith("- ", StringComparison.Ordinal) && indent > toolBlockIndent)
                {
                    count++;
                    counts[currentKey] = count;
                    continue;
                }

                // A same-or-shallower non-list line ends the tools block.
                if (!string.IsNullOrWhiteSpace(trimmed) && indent <= toolBlockIndent)
                {
                    inToolsBlock = false;
                }
            }

            return counts;
        });

    // ─── Router factory (mirrors RetailOpsRouterTests wiring) ─────────────

    private static RetailOpsRouter CreateRouter(string mockedLlmIntent)
    {
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant,
                    $"{{\"intent\":\"{mockedLlmIntent}\",\"confidence\":0.95,\"intents\":[\"{mockedLlmIntent}\"]}}")));

        List<ISpecialistAgent> specialists = CreateSpecialistsWithAllIntents();

        var routerDef = new AgentDefinition
        {
            Name = "Router",
            Model = "gpt-5.4-mini",
            SystemPrompt = "Classify user intent into retail categories. Return JSON.",
            Temperature = 0.1,
        };

        return new RetailOpsRouter(
            mockClient.Object,
            routerDef,
            specialists,
            Mock.Of<ILogger<RetailOpsRouter>>(),
            intentConfigs:
            [
                new RouterIntentConfig(
                    AgentIntent.PortfolioHealth,
                    ["how healthy is", "brand health report", "portfolio health",
                     "overall assessment for", "how is the portfolio"]),
                new RouterIntentConfig(
                    AgentIntent.Scorecard,
                    ["scorecard", "portfolio scoring", "brand ranking",
                     "top brand", "worst brand", "executive brief"]),
            ]);
    }

    /// <summary>
    /// Specialists covering every well-known intent, each owning exactly one intent
    /// and its own keyword fast-paths. Mirrors production wiring in Program.cs so
    /// the routing behaviour observed here matches what live users see.
    /// </summary>
    private static List<ISpecialistAgent> CreateSpecialistsWithAllIntents()
    {
        var general = new Mock<ISpecialistAgent>();
        general.Setup(s => s.Key).Returns("general");
        general.Setup(s => s.DisplayName).Returns("General Agent");
        general.Setup(s => s.SupportedIntents).Returns([AgentIntent.General]);
        general.Setup(s => s.KeywordFastPaths).Returns([]);

        var demand = new Mock<ISpecialistAgent>();
        demand.Setup(s => s.Key).Returns("demand-forecasting");
        demand.Setup(s => s.DisplayName).Returns("Demand Forecast Agent");
        demand.Setup(s => s.SupportedIntents).Returns([AgentIntent.DemandForecasting]);
        demand.Setup(s => s.KeywordFastPaths).Returns(
            ["depletion", "sell-through", "sell through", "velocity", "forecast", "demand"]);

        var promo = new Mock<ISpecialistAgent>();
        promo.Setup(s => s.Key).Returns("promo-planning");
        promo.Setup(s => s.DisplayName).Returns("Promo Planning Agent");
        promo.Setup(s => s.SupportedIntents).Returns([AgentIntent.PromotionTrade]);
        promo.Setup(s => s.KeywordFastPaths).Returns(
            ["promotion", "promo", "campaign", "lift", "trade spend"]);

        var supply = new Mock<ISpecialistAgent>();
        supply.Setup(s => s.Key).Returns("supply-chain");
        supply.Setup(s => s.DisplayName).Returns("Supply Chain Agent");
        supply.Setup(s => s.SupportedIntents).Returns([AgentIntent.SupplyShipments]);
        supply.Setup(s => s.KeywordFastPaths).Returns(
            ["supply", "shipment", "inventory", "disruption", "fulfillment", "pipeline"]);

        var competitive = new Mock<ISpecialistAgent>();
        competitive.Setup(s => s.Key).Returns("competitive-intel");
        competitive.Setup(s => s.DisplayName).Returns("Competitive Intel Agent");
        competitive.Setup(s => s.SupportedIntents).Returns([AgentIntent.CompetitiveMarket]);
        competitive.Setup(s => s.KeywordFastPaths).Returns(
            ["competitor", "market share", "competitive", "rival"]);

        var sentiment = new Mock<ISpecialistAgent>();
        sentiment.Setup(s => s.Key).Returns("field-sentiment");
        sentiment.Setup(s => s.DisplayName).Returns("Field Sentiment Agent");
        sentiment.Setup(s => s.SupportedIntents).Returns([AgentIntent.SentimentField]);
        sentiment.Setup(s => s.KeywordFastPaths).Returns(
            ["sentiment", "distributor feedback", "field feedback", "field sales", "retailer satisfaction"]);

        var council = new Mock<ISpecialistAgent>();
        council.Setup(s => s.Key).Returns("council");
        council.Setup(s => s.DisplayName).Returns("Consensus Council");
        council.Setup(s => s.SupportedIntents).Returns([AgentIntent.PortfolioHealth]);
        council.Setup(s => s.KeywordFastPaths).Returns([]);

        var planogram = new Mock<ISpecialistAgent>();
        planogram.Setup(s => s.Key).Returns("planogram");
        planogram.Setup(s => s.DisplayName).Returns("Planogram Agent");
        planogram.Setup(s => s.SupportedIntents).Returns([AgentIntent.Planogram]);
        planogram.Setup(s => s.KeywordFastPaths).Returns(
            ["planogram", "shelf layout", "shelf space", "facing", "sku placement", "brand blocking"]);

        var scorecard = new Mock<ISpecialistAgent>();
        scorecard.Setup(s => s.Key).Returns("scorecard");
        scorecard.Setup(s => s.DisplayName).Returns("Scorecard Agent");
        scorecard.Setup(s => s.SupportedIntents).Returns([AgentIntent.Scorecard]);
        scorecard.Setup(s => s.KeywordFastPaths).Returns(
            ["scorecard", "portfolio scoring", "brand ranking", "top brand", "worst brand", "executive brief"]);

        var storeOps = new Mock<ISpecialistAgent>();
        storeOps.Setup(s => s.Key).Returns("store-ops");
        storeOps.Setup(s => s.DisplayName).Returns("Store Ops Agent");
        storeOps.Setup(s => s.SupportedIntents).Returns([AgentIntent.StoreOps]);
        storeOps.Setup(s => s.KeywordFastPaths).Returns(
            ["store performance", "foot traffic", "conversion rate", "stockout", "underperforming store"]);

        var margin = new Mock<ISpecialistAgent>();
        margin.Setup(s => s.Key).Returns("margin-analysis");
        margin.Setup(s => s.DisplayName).Returns("Margin Agent");
        margin.Setup(s => s.SupportedIntents).Returns([AgentIntent.MarginAnalysis]);
        margin.Setup(s => s.KeywordFastPaths).Returns(
            ["margin", "profitability", "cogs", "gross margin", "net margin"]);

        return
        [
            general.Object,
            demand.Object,
            promo.Object,
            supply.Object,
            competitive.Object,
            sentiment.Object,
            council.Object,
            planogram.Object,
            scorecard.Object,
            storeOps.Object,
            margin.Object,
        ];
    }

    private static string ResolveRepoRelativePath(params string[] segments)
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12; i++)
        {
            if (File.Exists(Path.Combine(dir, "README.md"))
                && Directory.Exists(Path.Combine(dir, "src")))
            {
                return Path.Combine([dir, .. segments]);
            }
            string? parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir) break;
            dir = parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate repository root from test binary directory: " + AppContext.BaseDirectory);
    }
}

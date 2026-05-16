using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Agents.Routing;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Tests.Integration;

/// <summary>
/// Demo readiness tests for the executive presentation.
/// 
/// Validates that every default/suggested question shown on the chat UI
/// (PROMPT_CATEGORIES in src/RetailPulse.Web/src/components/ChatPanel.tsx)
/// can be:
///   1. Classified by the router without exception.
///   2. Routed to a registered specialist (or General fallback) — no orphan intents.
///   3. Dispatched to its agent and produce a non-empty reply.
///
/// Mirrors the production specialist registration order from
/// RoutingServiceExtensions.AddAgentRouting so the first-wins lookup
/// inside RetailOpsRouter resolves to the same specialist the live API would.
/// </summary>
public class DemoReadinessTests
{
    /// <summary>
    /// Every default prompt rendered on the chat UI's empty state.
    /// Source of truth: src/RetailPulse.Web/src/components/ChatPanel.tsx
    /// (PROMPT_CATEGORIES). Keep in sync when prompts change.
    /// </summary>
    public static readonly TheoryData<string, string, string> DefaultUiPrompts = new()
    {
        // ── General Retail (📊) ─────────────────────────────────────────────
        { "general", "Compare depletion trends across all regions for this quarter", AgentIntent.DemandForecasting },
        { "general", "Which brands are growing fastest year-over-year across the portfolio?", AgentIntent.DemandForecasting },
        { "general", "Show me field sentiment for our top 3 brands in the Southeast", AgentIntent.SentimentField },

        // ── Grocery (🛒) ───────────────────────────────────────────────────
        { "grocery", "How are FreshMart depletions trending in the Northeast this quarter?", AgentIntent.DemandForecasting },
        { "grocery", "Compare Harvest Table vs FreshMart sell-through rates by region", AgentIntent.DemandForecasting },
        { "grocery", "What is the field sentiment for Harvest Table Meal Kits in the Midwest?", AgentIntent.SentimentField },

        // ── Quick-Serve Restaurants (🍔) ────────────────────────────────────
        { "qsr", "How is Apex Grill performing in the Southwest this quarter?", AgentIntent.DemandForecasting },
        { "qsr", "Compare Coastline Tacos vs Apex Grill depletions across all regions", AgentIntent.DemandForecasting },
        { "qsr", "What is the field sentiment for Coastline Tacos in the West Coast?", AgentIntent.SentimentField },

        // ── Home Improvement (🏠) ──────────────────────────────────────────
        { "home", "Show me Pinnacle Hardware depletion stats in the Midwest for Q1", AgentIntent.DemandForecasting },
        { "home", "How is Summit Outdoor performing in the Southeast vs West Coast?", AgentIntent.DemandForecasting },
        { "home", "What is the field sentiment for Pinnacle Hardware Power Tools in the Southwest?", AgentIntent.SentimentField },

        // ── Office Supply (📎) ─────────────────────────────────────────────
        { "office", "How are ClearDesk depletions trending in the Northeast this quarter?", AgentIntent.DemandForecasting },
        { "office", "Compare ClearDesk Technology vs Paper Products sell-through by region", AgentIntent.DemandForecasting },
        { "office", "What is the field sentiment for ClearDesk in the Southeast?", AgentIntent.SentimentField },

        // ── Furniture (🛋️) ────────────────────────────────────────────────
        { "furniture", "Show me Urban Living depletion trends across all regions this quarter", AgentIntent.DemandForecasting },
        { "furniture", "Compare Foundry Home vs Urban Living performance in the West Coast", AgentIntent.DemandForecasting },
        { "furniture", "What is the field sentiment for Urban Living in the Pacific Northwest?", AgentIntent.SentimentField },

        // ── Charts (📈) ────────────────────────────────────────────────────
        { "charts", "Create a line chart showing Sierra Gold Tequila depletion trends across all regions", AgentIntent.DemandForecasting },
        { "charts", "Show me a bar chart comparing depletion velocity for all spirits brands in the Northeast", AgentIntent.DemandForecasting },
        { "charts", "Create a pie chart showing market share breakdown for our grocery brands nationally", AgentIntent.CompetitiveMarket },
        { "charts", "Show a grouped bar chart comparing FreshMart and Harvest Table across all regions", AgentIntent.DemandForecasting },
        { "charts", "Create a donut chart of Apex Grill variant mix in the Southwest", AgentIntent.DemandForecasting },
        { "charts", "Show a horizontal bar chart ranking all brands by depletion growth rate", AgentIntent.DemandForecasting },
        { "charts", "Create a table showing depletion stats for all home improvement brands by region", AgentIntent.DemandForecasting },
        { "charts", "Show a gauge chart for Pinnacle Hardware inventory health in the Midwest", AgentIntent.SupplyShipments },
    };

    [Theory]
    [MemberData(nameof(DefaultUiPrompts))]
    public async Task DefaultPrompt_RoutesToRegisteredSpecialist_AndReturnsNonEmptyReply(
        string category, string prompt, string expectedIntent)
    {
        _ = category;

        IChatClient routerClient = MockChatClient(
            $"{{\"intent\":\"{expectedIntent}\",\"confidence\":0.9,\"intents\":[\"{expectedIntent}\"]}}");

        (RetailOpsRouter? router, IReadOnlyList<ISpecialistAgent>? specialists) = BuildProductionLikePipeline(routerClient);

        RoutingDecision decision = await router.RouteAsync(prompt, null, null, null);

        decision.Should().NotBeNull($"router must classify default prompt: '{prompt}'");
        decision.Confidence.Should().BeGreaterThanOrEqualTo(0.6, "demo prompts should be high-confidence");

        ISpecialistAgent? specialist = specialists.FirstOrDefault(s =>
            string.Equals(s.Key, decision.AgentKey, StringComparison.OrdinalIgnoreCase));

        specialist.Should().NotBeNull($"a specialist must be registered for routing key '{decision.AgentKey}'");

        Contracts.ChatResponse response = await specialist.HandleAsync(
            new ChatRequest(prompt, SessionId: "demo-readiness"));

        response.Should().NotBeNull();
        response.Reply.Should().NotBeNullOrWhiteSpace($"agent '{specialist.Key}' must produce a reply for '{prompt}'");
    }

    [Fact]
    public void IntentCoverage_IsDocumentedAndNoOrphans()
    {
        (RetailOpsRouter _, IReadOnlyList<ISpecialistAgent>? specialists) = BuildProductionLikePipeline(MockChatClient("{}"));
        var supported = specialists
            .SelectMany(s => s.SupportedIntents)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Intents intentionally handled by routing fallback or out-of-band:
        //   - council/health  → intercepted in ChatEndpoints, dispatched to IConsensusCouncil
        //   - scorecard/portfolio → no dedicated specialist; router falls back to General
        var fallbackOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AgentIntent.PortfolioHealth,
            AgentIntent.Scorecard,
        };

        foreach (string intent in AgentIntent.All)
        {
            if (fallbackOnly.Contains(intent)) continue;

            supported.Should().Contain(intent,
                $"intent '{intent}' must be claimed by a specialist or General agent — " +
                "an unclaimed intent silently falls back to general/fallback at runtime");
        }
    }

    [Theory]
    [InlineData("scorecard/portfolio")]
    [InlineData("council/health")]
    public async Task UnclaimedIntent_RouterFallsBackToGeneral_NotException(string intent)
    {
        IChatClient routerClient = MockChatClient(
            $"{{\"intent\":\"{intent}\",\"confidence\":0.9,\"intents\":[\"{intent}\"]}}");

        (RetailOpsRouter? router, IReadOnlyList<ISpecialistAgent> _) = BuildProductionLikePipeline(routerClient);

        RoutingDecision decision = await router.RouteAsync("anything", null, null, null);

        decision.AgentKey.Should().Be("general",
            $"intent '{intent}' has no registered specialist in this pipeline — must fall back gracefully");
    }

    [Fact]
    public async Task LowConfidenceClassification_FallsBackToGeneral_NotException()
    {
        IChatClient routerClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.DemandForecasting}\",\"confidence\":0.3,\"intents\":[\"{AgentIntent.DemandForecasting}\"]}}");

        (RetailOpsRouter? router, IReadOnlyList<ISpecialistAgent> _) = BuildProductionLikePipeline(routerClient);

        RoutingDecision decision = await router.RouteAsync("vague question", null, null, null);

        decision.AgentKey.Should().Be("general", "low-confidence classifications must degrade gracefully");
        decision.Intent.Should().Be(AgentIntent.General);
    }

    [Fact]
    public async Task RouterClassificationFailure_FallsBackToGeneral_NotException()
    {
        var failingClient = new Mock<IChatClient>();
        failingClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("simulated upstream timeout"));

        (RetailOpsRouter? router, IReadOnlyList<ISpecialistAgent> _) = BuildProductionLikePipeline(failingClient.Object);

        RoutingDecision decision = await router.RouteAsync("anything", null, null, null);

        decision.AgentKey.Should().Be("general",
            "model timeouts must NOT crash the chat endpoint during a live demo");
        decision.Intent.Should().Be(AgentIntent.General);
    }

    [Fact]
    public async Task MalformedRouterJson_FallsBackToGeneral_NotException()
    {
        IChatClient malformedClient = MockChatClient("not valid json {{{");
        (RetailOpsRouter? router, IReadOnlyList<ISpecialistAgent> _) = BuildProductionLikePipeline(malformedClient);

        RoutingDecision decision = await router.RouteAsync("anything", null, null, null);

        decision.AgentKey.Should().Be("general");
        decision.Intent.Should().Be(AgentIntent.General);
    }

    [Theory]
    [MemberData(nameof(DefaultUiPrompts))]
    public void DefaultPrompt_IsWellFormed(string category, string prompt, string expectedIntent)
    {
        _ = category;
        _ = expectedIntent;

        prompt.Should().NotBeNullOrWhiteSpace();
        prompt.Length.Should().BeInRange(20, 200,
            "demo prompts should be specific but not overwhelming for the model");
        prompt.Should().NotContain("TODO", "no placeholder text in shipped UI prompts");
        prompt.Should().NotContain("XXX");
    }

    /// <summary>
    /// Mirrors RoutingServiceExtensions.AddAgentRouting registration order
    /// (General first, then Demand, Promo, Competitive, Supply, StoreOps,
    /// Planogram, Margin, Memory). RetailOpsRouter uses TryAdd — first
    /// specialist that claims an intent wins. This shape reproduces the
    /// production binding so demo-readiness tests reflect what the live
    /// API would actually do.
    /// </summary>
    private static (RetailOpsRouter Router, IReadOnlyList<ISpecialistAgent> Specialists)
        BuildProductionLikePipeline(IChatClient routerClient)
    {
        IHubContext<TelemetryHub> hubContext = CreateMockHubContext();
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();
        var pipeline = new AgentExecutionPipeline(
            MockChatClient("agent reply"),
            hubContext,
            config,
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());

        var def = new AgentDefinition
        {
            Name = "Test",
            Model = "gpt-5.4-mini",
            SystemPrompt = "Test system prompt.",
            Temperature = 0.3
        };

        // Production-order registration: General first, then specialists.
        // GeneralAgent only claims General; FieldSentimentAgent owns SentimentField;
        // other dedicated specialists own their domain intents (PromotionTrade,
        // SupplyShipments, CompetitiveMarket, etc.) and are routed to directly.
        var specialists = new List<ISpecialistAgent>
        {
            new GeneralAgent(pipeline, def, []),
            new FieldSentimentAgent(pipeline, def, []),
            new DemandForecastAgent(pipeline, def, []),
            new PromoPlanningAgent(pipeline, def, [], null),
            new CompetitiveIntelAgent(pipeline, def, [], hubContext,
                NullLoggerFactory.Instance.CreateLogger<CompetitiveIntelAgent>(), null),
            new SupplyChainAgent(pipeline, def, []),
            new StoreOpsAgent(pipeline, def, []),
            new PlanogramAgent(pipeline, def, []),
            new MarginAgent(pipeline, def, []),
            new MemoryManagementAgent(
                Mock.Of<Contracts.Memory.IConversationMemory>(),
                NullLoggerFactory.Instance.CreateLogger<MemoryManagementAgent>()),
        };

        var routerDef = new AgentDefinition
        {
            Name = "Router",
            Model = "gpt-5.4-mini",
            SystemPrompt = "Classify intent.",
            Temperature = 0.1
        };

        var router = new RetailOpsRouter(
            routerClient, routerDef, specialists,
            Mock.Of<ILogger<RetailOpsRouter>>());

        return (router, specialists);
    }

    private static IChatClient MockChatClient(string responseText)
    {
        var mock = new Mock<IChatClient>();
        mock
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant, responseText)));
        return mock.Object;
    }

    private static IHubContext<TelemetryHub> CreateMockHubContext()
    {
        var hubContext = new Mock<IHubContext<TelemetryHub>>();
        var clients = new Mock<IHubClients>();
        var groupProxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(groupProxy.Object);
        hubContext.Setup(h => h.Clients).Returns(clients.Object);
        return hubContext.Object;
    }
}

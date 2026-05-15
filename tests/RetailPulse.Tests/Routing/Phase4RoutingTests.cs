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

namespace RetailPulse.Tests.Routing;

/// <summary>
/// Phase 4 routing integration tests: verifies that Phase 4 intents
/// (store-ops, planogram, margin, scorecard) route correctly,
/// and that all existing intents still route as expected (no regression).
/// </summary>
public class Phase4RoutingTests
{
    #region Phase 4 Intent Routing

    [Theory]
    [InlineData("What's the store performance in the Northeast?")]
    [InlineData("Show me store metrics for the top 5 stores")]
    [InlineData("How are our stores performing this quarter?")]
    public async Task StorePerformanceQueries_RouteToStoreOpsAgent(string message)
    {
        var router = CreateRouterForIntent(AgentIntent.StoreOps, "store-ops");

        var result = await router.RouteAsync(message, null, null, null);

        result.Intent.Should().Be(AgentIntent.StoreOps);
        result.AgentKey.Should().Be("store-ops");
        result.Confidence.Should().BeGreaterThan(0.6);
    }

    [Theory]
    [InlineData("Optimize the planogram for aisle A1")]
    [InlineData("Rearrange shelf layout for maximum velocity")]
    [InlineData("What's the best planogram for our spirits section?")]
    public async Task PlanogramQueries_RouteToPlanogramAgent(string message)
    {
        var router = CreateRouterForIntent(AgentIntent.Planogram, "planogram");

        var result = await router.RouteAsync(message, null, null, null);

        result.Intent.Should().Be(AgentIntent.Planogram);
        result.AgentKey.Should().Be("planogram");
        result.Confidence.Should().BeGreaterThan(0.6);
    }

    [Theory]
    [InlineData("What's the margin analysis for Sierra Gold Tequila?")]
    [InlineData("Show me P&L breakdown by brand")]
    [InlineData("Analyze margin trends across the portfolio")]
    public async Task MarginQueries_RouteToMarginAgent(string message)
    {
        var router = CreateRouterForIntent(AgentIntent.MarginAnalysis, "margin");

        var result = await router.RouteAsync(message, null, null, null);

        result.Intent.Should().Be(AgentIntent.MarginAnalysis);
        result.AgentKey.Should().Be("margin");
        result.Confidence.Should().BeGreaterThan(0.6);
    }

    [Theory]
    [InlineData("Generate the portfolio scorecard")]
    [InlineData("Show me brand health scores across all dimensions")]
    [InlineData("Portfolio health dashboard with trend analysis")]
    public async Task ScorecardQueries_RouteToScorecard(string message)
    {
        var router = CreateRouterForIntent(AgentIntent.Scorecard, "scorecard");

        var result = await router.RouteAsync(message, null, null, null);

        result.Intent.Should().Be(AgentIntent.Scorecard);
        result.AgentKey.Should().Be("scorecard");
        result.Confidence.Should().BeGreaterThan(0.6);
    }

    #endregion

    #region Regression — Existing Intents Still Route Correctly

    [Fact]
    public async Task DemandIntents_StillRouteCorrectly()
    {
        var router = CreateRouterForIntent(AgentIntent.DemandForecasting, "demand-forecasting");

        var result = await router.RouteAsync("What's the demand forecast?", null, null, null);

        result.Intent.Should().Be(AgentIntent.DemandForecasting);
        result.AgentKey.Should().Be("demand-forecasting");
    }

    [Fact]
    public async Task PromoIntents_StillRouteCorrectly()
    {
        var router = CreateRouterForIntent(AgentIntent.PromotionTrade, "promo-planning");

        var result = await router.RouteAsync("Plan a promotion for next quarter", null, null, null);

        result.Intent.Should().Be(AgentIntent.PromotionTrade);
        result.AgentKey.Should().Be("promo-planning");
    }

    [Fact]
    public async Task SupplyIntents_StillRouteCorrectly()
    {
        var router = CreateRouterForIntent(AgentIntent.SupplyShipments, "supply-chain");

        var result = await router.RouteAsync("Check supply chain status", null, null, null);

        result.Intent.Should().Be(AgentIntent.SupplyShipments);
        result.AgentKey.Should().Be("supply-chain");
    }

    [Fact]
    public async Task CompetitiveIntents_StillRouteCorrectly()
    {
        var router = CreateRouterForIntent(AgentIntent.CompetitiveMarket, "competitive-intel");

        var result = await router.RouteAsync("Analyze competitor pricing", null, null, null);

        result.Intent.Should().Be(AgentIntent.CompetitiveMarket);
        result.AgentKey.Should().Be("competitive-intel");
    }

    [Fact]
    public async Task GeneralIntents_StillRouteCorrectly()
    {
        var router = CreateRouterForIntent(AgentIntent.General, "general");

        var result = await router.RouteAsync("Hello, what can you do?", null, null, null);

        result.Intent.Should().Be(AgentIntent.General);
        result.AgentKey.Should().Be("general");
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Creates a router that returns a fixed intent classification for any query.
    /// This tests that the router correctly maps intents to agent keys.
    /// </summary>
    private static RetailOpsRouter CreateRouterForIntent(string intent, string agentKey)
    {
        var json = $"{{\"intent\":\"{intent}\",\"confidence\":0.92,\"intents\":[\"{intent}\"]}}";
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant, json)));

        // Create a specialist that handles the target intent
        var specialist = new Mock<ISpecialistAgent>();
        specialist.Setup(a => a.Key).Returns(agentKey);
        specialist.Setup(a => a.SupportedIntents).Returns([intent]);
        specialist.Setup(a => a.HandleAsync(
                It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Contracts.ChatResponse("response", "session", []));

        // Also include a general fallback agent
        var generalAgent = CreateGeneralAgent();

        var specialists = new List<ISpecialistAgent> { specialist.Object, generalAgent };

        var routerDef = new AgentDefinition
        {
            Name = "Router",
            Model = "gpt-5.4-mini",
            SystemPrompt = "Classify intent. Return JSON with intent, confidence, reasoning.",
            Temperature = 0.1
        };

        return new RetailOpsRouter(
            mockClient.Object, routerDef, specialists,
            Mock.Of<ILogger<RetailOpsRouter>>());
    }

    private static GeneralAgent CreateGeneralAgent()
    {
        var hubContext = new Mock<IHubContext<TelemetryHub>>();
        var clients = new Mock<IHubClients>();
        var groupProxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(groupProxy.Object);
        hubContext.Setup(h => h.Clients).Returns(clients.Object);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        var pipeline = new AgentExecutionPipeline(
            Mock.Of<IChatClient>(),
            hubContext.Object,
            config,
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());

        return new GeneralAgent(
            pipeline,
            new AgentDefinition { Name = "General", Model = "gpt-4o", SystemPrompt = "Test", Temperature = 0.7 },
            []);
    }

    #endregion
}

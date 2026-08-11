using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Agents.Routing;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;
using Xunit;

namespace RetailPulse.Tests.Agents.Router;

/// <summary>
/// Regression coverage for issue #74 — the P0 "Show a horizontal bar chart ranking
/// all brands by depletion growth rate" prompt was routed to the DemandForecasting
/// specialist (matched on the "depletion" cue), whose tool set does not include
/// <c>GetPortfolioDepletionStats</c>. That forced a per-brand fan-out that blew the
/// tool-call budget and led to a zero-valued horizontalBar shell surfacing on the
/// frontend. These tests pin the fix: portfolio-ranking / growth-rate prompts must
/// route to the General agent (which owns the aggregate tool) regardless of the
/// specific brand list, region wording, or the presence of the word "depletion".
/// Rules are intent-shape only — no brand or tenant literals.
/// </summary>
public sealed class ChartRankingRoutingTests
{
    /// <summary>
    /// Exact P0 phrase. Must reach the General/portfolio agent, NOT DemandForecasting.
    /// </summary>
    [Fact]
    public async Task RouteAsync_HorizontalBarRankByDepletionGrowth_RoutesToGeneralAgentWithPortfolioTool()
    {
        RetailOpsRouter router = CreateRouter();

        RoutingDecision result = await router.RouteAsync(
            "Show a horizontal bar chart ranking all brands by depletion growth rate",
            null, null, null);

        result.AgentKey.Should().Be("general");
        result.Intent.Should().Be(AgentIntent.General);
        result.Intent.Should().NotBe(AgentIntent.DemandForecasting,
            "the ranking/growth cue must beat the 'depletion' cue in ChartRequestDetector");
    }

    /// <summary>
    /// Generic ranking / growth-rate phrasings — none of which name a tenant brand —
    /// must all route to the aggregate-capable General agent.
    /// </summary>
    [Theory]
    [InlineData("rank all brands by growth rate as a horizontal bar chart")]
    [InlineData("show a horizontal bar chart of top brands by depletion growth")]
    [InlineData("horizontal bar chart of the fastest growing brands")]
    [InlineData("bar chart comparing all brands by YoY growth")]
    [InlineData("horizontal bar chart cross-brand ranking of depletion growth")]
    public async Task RouteAsync_RankingOrGrowthPhrasing_RoutesToGeneralAgent(string message)
    {
        RetailOpsRouter router = CreateRouter();

        RoutingDecision result = await router.RouteAsync(message, null, null, null);

        result.Intent.Should().Be(AgentIntent.General,
            $"ranking/growth phrasing '{message}' must route to the aggregate-capable agent");
        result.AgentKey.Should().Be("general");
    }

    /// <summary>
    /// Companion negative case: a bare velocity comparison — no ranking / growth
    /// language — should still route to DemandForecasting via the depletion/velocity
    /// cue, proving the new ranking cue is narrowly scoped and did not swallow every
    /// depletion-related chart request.
    /// </summary>
    [Fact]
    public async Task RouteAsync_VelocityBarChart_StillRoutesToDemandForecasting()
    {
        RetailOpsRouter router = CreateRouter();

        RoutingDecision result = await router.RouteAsync(
            "Show me a bar chart comparing depletion velocity for all spirits brands in the Northeast",
            null, null, null);

        result.Intent.Should().Be(AgentIntent.DemandForecasting);
    }

    private static RetailOpsRouter CreateRouter()
    {
        // Cover all intents that a real production router would carry.
        var general = new Mock<ISpecialistAgent>();
        general.Setup(s => s.Key).Returns("general");
        general.Setup(s => s.DisplayName).Returns("General Agent");
        general.Setup(s => s.SupportedIntents).Returns([AgentIntent.General]);

        var demand = new Mock<ISpecialistAgent>();
        demand.Setup(s => s.Key).Returns("demand-forecasting");
        demand.Setup(s => s.DisplayName).Returns("Demand Forecasting");
        demand.Setup(s => s.SupportedIntents).Returns([AgentIntent.DemandForecasting]);

        var chatClient = new Mock<IChatClient>();
        chatClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant,
                    $"{{\"intent\":\"{AgentIntent.General}\",\"confidence\":0.5}}")));

        var routerDef = new AgentDefinition
        {
            Name = "Router",
            Model = "gpt-5.4-mini",
            SystemPrompt = "Classify user intent.",
            Temperature = 0.1
        };

        return new RetailOpsRouter(
            chatClient.Object,
            routerDef,
            [general.Object, demand.Object],
            Mock.Of<ILogger<RetailOpsRouter>>());
    }
}

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
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Tests.Agents.Router;

/// <summary>
/// Smoke tests for demo-critical queries that must never break.
/// Verifies the full path: keyword fast-path routing → specialist lookup → agent execution → non-empty reply.
/// Added as a regression gate after "How is Apex Grill performing in the Southwest this quarter?"
/// returned empty in the UI (2026-05-16 incident).
/// </summary>
public class DemoQuerySmokeTests
{
    private const string ApexGrillQuery = "How is Apex Grill performing in the Southwest this quarter?";

    #region Routing: Keyword Fast-Path

    [Fact]
    public async Task ApexGrillQuery_RoutesViaKeywordFastPath_ToGeneralIntent()
    {
        // The BrandPerformingRegex should match this query and route directly to General
        // without calling the LLM. This test guards against the regex being accidentally
        // removed or modified in a way that breaks routing for this demo-critical query.
        Mock<IChatClient> mockClient = CreateTrackedMockChatClient();
        RetailOpsRouter router = CreateRouterWithGeneralSpecialist(mockClient.Object);

        RoutingDecision decision = await router.RouteAsync(ApexGrillQuery, null, null, null);

        decision.Intent.Should().Be(AgentIntent.General,
            "BrandPerformingRegex must route single-brand performance queries to General intent");
        decision.Confidence.Should().Be(0.95,
            "keyword fast-path assigns 0.95 confidence — must not degrade");
        decision.AgentKey.Should().Be("general",
            "routing decision must resolve to a registered specialist key");

        // Critical: LLM must NOT be called — this verifies the fast-path is active
        mockClient.Verify(x => x.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()), Times.Never,
            "keyword fast-path must bypass LLM classification entirely");
    }

    [Theory]
    [InlineData("How is Apex Grill performing in the Southwest this quarter?")]
    [InlineData("How is Coastline Tacos doing in the West Coast?")]
    [InlineData("How is FreshMart performing in the Northeast?")]
    [InlineData("How is Pinnacle Hardware doing this quarter?")]
    public async Task BrandPerformingQueries_AllRouteViaKeywordFastPath(string query)
    {
        // All single-brand performance queries must hit BrandPerformingRegex.
        // This prevents regression if the regex pattern is narrowed.
        Mock<IChatClient> mockClient = CreateTrackedMockChatClient();
        RetailOpsRouter router = CreateRouterWithGeneralSpecialist(mockClient.Object);

        RoutingDecision decision = await router.RouteAsync(query, null, null, null);

        decision.Intent.Should().Be(AgentIntent.General);
        decision.Confidence.Should().Be(0.95);
        decision.AgentKey.Should().NotBeNullOrEmpty();

        mockClient.Verify(x => x.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApexGrillQuery_DoesNotMatchPortfolioRegex()
    {
        // Ensure the portfolio regex doesn't accidentally capture single-brand queries.
        // If it did, the query would go to the Consensus Council (4+ LLM roundtrips)
        // instead of GeneralAgent (1 tool call) — causing timeouts in demo.
        Mock<IChatClient> mockClient = CreateTrackedMockChatClient();
        RetailOpsRouter router = CreateRouterWithAllSpecialists(mockClient.Object);

        RoutingDecision decision = await router.RouteAsync(ApexGrillQuery, null, null, null);

        decision.Intent.Should().NotBe(AgentIntent.PortfolioHealth,
            "single-brand queries must NOT route to the Consensus Council");
    }

    #endregion

    #region Agent Execution: Non-Empty Response

    [Fact]
    public async Task ApexGrillQuery_GeneralAgentProducesNonEmptyResponse()
    {
        // End-to-end verification: route the query, dispatch to GeneralAgent,
        // and confirm a non-empty reply is returned. This catches the scenario
        // where routing succeeds but the agent swallows the response.
        string expectedReply = "Apex Grill showed 12% growth in the Southwest this quarter with strong depletion trends across all SKUs.";
        IChatClient agentClient = MockAgentChatClient(expectedReply);
        IHubContext<TelemetryHub> hubContext = CreateMockHubContext();
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        AgentExecutionPipeline pipeline = new(
            agentClient,
            hubContext,
            config,
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());

        AgentDefinition agentDef = new()
        {
            Name = "General",
            Model = "gpt-5.4-mini",
            SystemPrompt = "You are a retail analytics assistant.",
            Temperature = 0.3
        };

        GeneralAgent generalAgent = new(pipeline, agentDef, []);

        ChatRequest request = new(ApexGrillQuery, SessionId: "smoke-test-session");
        ChatResponse response = await generalAgent.HandleAsync(request);

        response.Should().NotBeNull("GeneralAgent must return a response object");
        response.Reply.Should().NotBeNullOrWhiteSpace(
            "GeneralAgent must produce a non-empty reply for brand performance queries — " +
            "empty replies cause 'nothing renders' in the UI");
        response.Reply.Should().Contain("Apex Grill",
            "the reply should reference the queried brand");
    }

    [Fact]
    public async Task ApexGrillQuery_WhenLlmReturnsNull_FallbackReplyIsNotEmpty()
    {
        // Even if the LLM returns null text (e.g., MaxIterations exhausted after tool call),
        // the pipeline's FallbackReply mechanism must produce a non-empty response.
        IChatClient nullResponseClient = MockAgentChatClient(null);
        IHubContext<TelemetryHub> hubContext = CreateMockHubContext();
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        AgentExecutionPipeline pipeline = new(
            nullResponseClient,
            hubContext,
            config,
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());

        AgentDefinition agentDef = new()
        {
            Name = "General",
            Model = "gpt-5.4-mini",
            SystemPrompt = "You are a retail analytics assistant.",
            Temperature = 0.3
        };

        GeneralAgent generalAgent = new(pipeline, agentDef, []);

        ChatRequest request = new(ApexGrillQuery, SessionId: "smoke-test-fallback");
        ChatResponse response = await generalAgent.HandleAsync(request);

        response.Should().NotBeNull();
        response.Reply.Should().NotBeNullOrWhiteSpace(
            "even when the LLM returns null, the FallbackReply must prevent empty responses");
    }

    #endregion

    #region Full Pipeline: Routing + Dispatch

    [Fact]
    public async Task ApexGrillQuery_FullPipeline_RoutesAndProducesReply()
    {
        // Integration smoke test: router + specialist lookup + execution.
        // Mirrors the production flow in ChatEndpoints.cs.
        string agentReply = "Apex Grill is performing well in the Southwest with 8% YoY growth.";
        IChatClient agentClient = MockAgentChatClient(agentReply);
        IHubContext<TelemetryHub> hubContext = CreateMockHubContext();
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        AgentExecutionPipeline pipeline = new(
            agentClient,
            hubContext,
            config,
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());

        AgentDefinition agentDef = new()
        {
            Name = "General",
            Model = "gpt-5.4-mini",
            SystemPrompt = "You are a retail analytics assistant.",
            Temperature = 0.3
        };

        // Build production-like specialist list
        List<ISpecialistAgent> specialists =
        [
            new GeneralAgent(pipeline, agentDef, [])
        ];

        AgentDefinition routerDef = new()
        {
            Name = "Router",
            Model = "gpt-5.4-mini",
            SystemPrompt = "Classify intent.",
            Temperature = 0.1
        };

        Mock<IChatClient> routerClient = CreateTrackedMockChatClient();
        RetailOpsRouter router = new(
            routerClient.Object,
            routerDef,
            specialists,
            Mock.Of<ILogger<RetailOpsRouter>>());

        // Step 1: Route
        RoutingDecision decision = await router.RouteAsync(ApexGrillQuery, null, null, null);
        decision.AgentKey.Should().NotBeNullOrEmpty();

        // Step 2: Find specialist (same as ChatEndpoints.cs line 143-144)
        ISpecialistAgent? specialist = specialists.FirstOrDefault(s =>
            string.Equals(s.Key, decision.AgentKey, StringComparison.OrdinalIgnoreCase));
        specialist.Should().NotBeNull("a specialist must exist for the routed AgentKey");

        // Step 3: Execute
        ChatRequest request = new(ApexGrillQuery, SessionId: "smoke-full-pipeline");
        ChatResponse response = await specialist.HandleAsync(request);

        response.Reply.Should().NotBeNullOrWhiteSpace(
            "the full pipeline (route → select → execute) must produce a non-empty reply");
    }

    #endregion

    #region Helpers

    private static Mock<IChatClient> CreateTrackedMockChatClient()
    {
        Mock<IChatClient> mock = new();
        mock
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant,
                    $"{{\"intent\":\"{AgentIntent.General}\",\"confidence\":0.8}}")));
        return mock;
    }

    private static IChatClient MockAgentChatClient(string? responseText)
    {
        Mock<IChatClient> mock = new();
        mock
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant, responseText)));
        return mock.Object;
    }

    private static RetailOpsRouter CreateRouterWithGeneralSpecialist(IChatClient routerClient)
    {
        Mock<ISpecialistAgent> general = new();
        general.Setup(s => s.Key).Returns("general");
        general.Setup(s => s.DisplayName).Returns("General Agent");
        general.Setup(s => s.SupportedIntents).Returns([AgentIntent.General]);

        AgentDefinition routerDef = new()
        {
            Name = "Router",
            Model = "gpt-5.4-mini",
            SystemPrompt = "Classify intent.",
            Temperature = 0.1
        };

        return new RetailOpsRouter(
            routerClient,
            routerDef,
            [general.Object],
            Mock.Of<ILogger<RetailOpsRouter>>());
    }

    private static RetailOpsRouter CreateRouterWithAllSpecialists(IChatClient routerClient)
    {
        Mock<ISpecialistAgent> general = new();
        general.Setup(s => s.Key).Returns("general");
        general.Setup(s => s.DisplayName).Returns("General Agent");
        general.Setup(s => s.SupportedIntents).Returns([AgentIntent.General]);

        Mock<ISpecialistAgent> council = new();
        council.Setup(s => s.Key).Returns("council");
        council.Setup(s => s.DisplayName).Returns("Consensus Council");
        council.Setup(s => s.SupportedIntents).Returns([AgentIntent.PortfolioHealth]);

        Mock<ISpecialistAgent> demand = new();
        demand.Setup(s => s.Key).Returns("demand-forecast");
        demand.Setup(s => s.DisplayName).Returns("Demand Forecast Agent");
        demand.Setup(s => s.SupportedIntents).Returns([AgentIntent.DemandForecasting]);

        AgentDefinition routerDef = new()
        {
            Name = "Router",
            Model = "gpt-5.4-mini",
            SystemPrompt = "Classify intent.",
            Temperature = 0.1
        };

        return new RetailOpsRouter(
            routerClient,
            routerDef,
            [general.Object, council.Object, demand.Object],
            Mock.Of<ILogger<RetailOpsRouter>>());
    }

    private static IHubContext<TelemetryHub> CreateMockHubContext()
    {
        Mock<IHubContext<TelemetryHub>> hubContext = new();
        Mock<IHubClients> clients = new();
        Mock<IClientProxy> groupProxy = new();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(groupProxy.Object);
        hubContext.Setup(h => h.Clients).Returns(clients.Object);
        return hubContext.Object;
    }

    #endregion
}

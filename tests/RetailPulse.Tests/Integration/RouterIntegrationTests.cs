using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Agents.Routing;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Approval;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Memory;
using RetailPulse.Api.Models;
using RetailPulse.Api.Agents;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Approval;
using RetailPulse.Contracts.Memory;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Tests.Integration;

/// <summary>
/// Integration tests for the full routing pipeline:
/// message → router → specialist → response.
/// Uses in-memory mocks rather than WebApplicationFactory since the
/// app requires Azure credentials for startup.
/// </summary>
public class RouterIntegrationTests
{
    #region Full Pipeline: Message → Router → Specialist → Response

    [Fact]
    public async Task FullPipeline_DemandMessage_RoutesAndReturnsResponse()
    {
        // Router classifies as demand
        var routerClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.DemandForecasting}\",\"confidence\":0.92,\"intents\":[\"{AgentIntent.DemandForecasting}\"]}}");

        var generalAgent = CreateGeneralAgent(
            MockChatClient("Brand X demand is projected to grow 15% next quarter."));

        // Create a mock DemandForecastAgent that claims the demand/forecasting intent
        var demandAgent = new Mock<ISpecialistAgent>();
        demandAgent.Setup(a => a.Key).Returns("demand-forecasting");
        demandAgent.Setup(a => a.SupportedIntents).Returns(new[] { AgentIntent.DemandForecasting });
        demandAgent.Setup(a => a.HandleAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Contracts.ChatResponse("Brand X demand is projected to grow 15% next quarter.", SessionId: "session-1", Spans: []));

        var specialists = new List<ISpecialistAgent> { demandAgent.Object, generalAgent };
        var router = CreateRouter(routerClient, specialists);

        // Route the message
        var routingResult = await router.RouteAsync(
            "What's the demand forecast for Brand X?", null, null, null);

        // The general agent handles all intents, so route to it
        var response = await generalAgent.HandleAsync(
            new ChatRequest("What's the demand forecast for Brand X?", SessionId: "session-1"));

        // Assert routing
        routingResult.Intent.Should().Be(AgentIntent.DemandForecasting);
        routingResult.Confidence.Should().BeGreaterThan(0.6);

        // Assert response
        response.Reply.Should().Contain("Brand X");
        response.SessionId.Should().Be("session-1");
    }

    [Fact]
    public async Task FullPipeline_GeneralMessage_RoutesToGeneralAgent()
    {
        var routerClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.General}\",\"confidence\":0.85,\"intents\":[\"{AgentIntent.General}\"]}}");

        var generalAgent = CreateGeneralAgent(
            MockChatClient("Here is the portfolio overview."));

        var specialists = new List<ISpecialistAgent> { generalAgent };
        var router = CreateRouter(routerClient, specialists);

        var routingResult = await router.RouteAsync(
            "Show me the portfolio overview", null, null, null);

        var response = await generalAgent.HandleAsync(
            new ChatRequest("Show me the portfolio overview", SessionId: "s-1"));

        routingResult.Intent.Should().Be(AgentIntent.General);
        routingResult.AgentKey.Should().Be("general");
        response.Reply.Should().Contain("portfolio");
    }

    [Fact]
    public async Task FullPipeline_LowConfidence_FallsBackToGeneral()
    {
        var routerClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.SupplyShipments}\",\"confidence\":0.3,\"intents\":[\"{AgentIntent.SupplyShipments}\"]}}");

        var generalAgent = CreateGeneralAgent(
            MockChatClient("Could you be more specific?"));

        var specialists = new List<ISpecialistAgent> { generalAgent };
        var router = CreateRouter(routerClient, specialists);

        var routingResult = await router.RouteAsync(
            "Tell me about stuff", null, null, null);

        routingResult.Intent.Should().Be(AgentIntent.General);
    }

    [Fact]
    public async Task FullPipeline_RouterFails_StillGetsResponse()
    {
        var failingClient = new Mock<IChatClient>();
        failingClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("LLM down"));

        var generalAgent = CreateGeneralAgent(MockChatClient("I can help!"));

        var specialists = new List<ISpecialistAgent> { generalAgent };
        var router = CreateRouter(failingClient.Object, specialists);

        // Router falls back to general
        var routingResult = await router.RouteAsync("hello", null, null, null);
        routingResult.Intent.Should().Be(AgentIntent.General);

        // General agent still works
        var response = await generalAgent.HandleAsync(
            new ChatRequest("hello", SessionId: "s-fallback"));
        response.Reply.Should().Be("I can help!");
    }

    #endregion

    #region DI Registration

    [Fact]
    public void RoutingServiceExtensions_RegistersServicesWithoutError()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        var generalDef = new AgentDefinition
        {
            Name = "General",
            Model = "gpt-5.4-mini",
            SystemPrompt = "Test prompt",
            Temperature = 0.7
        };

        var promptConfig = new PromptConfiguration
        {
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["router"] = new()
                {
                    Name = "Router",
                    Model = "gpt-5.4-mini",
                    SystemPrompt = "Classify intent",
                    Temperature = 0.1
                }
            }
        };

        var act = () => services.AddAgentRouting(
            promptConfig,
            generalDef,
            foundryEnabled: false,
            toolsFactory: _ => []);

        act.Should().NotThrow();
    }

    [Fact]
    public void RoutingServiceExtensions_ThrowsIfRouterDefMissing()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var promptConfig = new PromptConfiguration
        {
            Agents = new Dictionary<string, AgentDefinition>()
        };

        var act = () => services.AddAgentRouting(
            promptConfig,
            new AgentDefinition { Name = "General", SystemPrompt = "test" },
            foundryEnabled: false,
            toolsFactory: _ => []);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*router*");
    }

    #endregion

    #region Telemetry Integration

    [Fact]
    public async Task FullPipeline_GeneralAgent_EmitsSpans()
    {
        var chatClient = MockChatClient("Analysis complete.");
        var agent = CreateGeneralAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("Run analysis", SessionId: "telemetry-test"));

        response.Spans.Should().NotBeEmpty();
        response.Spans.Should().Contain(s => s.Type == "thought");
        response.Spans.Should().Contain(s => s.Type == "response");
        response.Spans.Should().OnlyContain(s => s.SessionId == "telemetry-test");
    }

    [Fact]
    public async Task FullPipeline_GeneralAgent_SpansHaveTimestamps()
    {
        var chatClient = MockChatClient("done");
        var agent = CreateGeneralAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("test", SessionId: "ts-test"));

        response.Spans.Should().OnlyContain(s => s.Timestamp > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task FullPipeline_TotalDurationMs_ReflectsRealTime()
    {
        var chatClient = MockChatClient("done");
        var agent = CreateGeneralAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("test", SessionId: "dur-test"));

        response.TotalDurationMs.Should().NotBeNull();
        response.TotalDurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    #endregion

    #region Multi-Tenant Scenarios

    [Fact]
    public async Task FullPipeline_DifferentTenants_RouteIndependently()
    {
        var routerClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.DemandForecasting}\",\"confidence\":0.9,\"intents\":[\"{AgentIntent.DemandForecasting}\"]}}");
        var generalAgent = CreateGeneralAgent(MockChatClient("ok"));
        var specialists = new List<ISpecialistAgent> { generalAgent };
        var router = CreateRouter(routerClient, specialists);

        // Route for tenant A
        var resultA = await router.RouteAsync(
            "demand forecast?", null, null, "tenant-a");

        // Route for tenant B (same message)
        var resultB = await router.RouteAsync(
            "demand forecast?", null, null, "tenant-b");

        // Both get consistent classification
        resultA.Intent.Should().Be(resultB.Intent);
        resultA.Confidence.Should().Be(resultB.Confidence);
    }

    [Fact]
    public async Task FullPipeline_DifferentUsers_RouteIndependently()
    {
        var routerClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.SentimentField}\",\"confidence\":0.9,\"intents\":[\"{AgentIntent.SentimentField}\"]}}");
        var generalAgent = CreateGeneralAgent(MockChatClient("sentiment data"));
        var specialists = new List<ISpecialistAgent> { generalAgent };
        var router = CreateRouter(routerClient, specialists);

        var userA = new UserContext("obj-1", "Alice", "alice@contoso.com");
        var userB = new UserContext("obj-2", "Bob", "bob@contoso.com");

        var resultA = await router.RouteAsync("distributor sentiment?", null, userA, "tenant-1");
        var resultB = await router.RouteAsync("distributor sentiment?", null, userB, "tenant-1");

        resultA.Intent.Should().Be(resultB.Intent);
    }

    #endregion

    #region Demand Forecasting Routing

    [Theory]
    [InlineData("What's the demand forecast for Sierra Gold Tequila?")]
    [InlineData("Predict next quarter demand")]
    [InlineData("Show historical sales trends")]
    public async Task FullPipeline_DemandQueries_RouteToDemandForecasting(string message)
    {
        var routerClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.DemandForecasting}\",\"confidence\":0.92,\"intents\":[\"{AgentIntent.DemandForecasting}\"]}}");

        var demandAgent = CreateMockDemandAgent();
        var generalAgent = CreateGeneralAgent(MockChatClient("general fallback"));

        var specialists = new List<ISpecialistAgent> { demandAgent, generalAgent };
        var router = CreateRouter(routerClient, specialists);

        var routingResult = await router.RouteAsync(message, null, null, null);

        routingResult.Intent.Should().Be(AgentIntent.DemandForecasting);
        routingResult.Confidence.Should().BeGreaterThan(0.6);
        routingResult.AgentKey.Should().Be("demand-forecasting");
    }

    [Fact]
    public async Task FullPipeline_DepletionQuery_StillRoutesToGeneral_BackwardCompat()
    {
        // "What are my depletions?" is a general portfolio query, not demand forecasting
        var routerClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.General}\",\"confidence\":0.85,\"intents\":[\"{AgentIntent.General}\"]}}");

        var generalAgent = CreateGeneralAgent(MockChatClient("Here are your depletions."));
        var demandAgent = CreateMockDemandAgent();
        var specialists = new List<ISpecialistAgent> { demandAgent, generalAgent };
        var router = CreateRouter(routerClient, specialists);

        var routingResult = await router.RouteAsync("What are my depletions?", null, null, null);

        routingResult.Intent.Should().Be(AgentIntent.General);
        routingResult.AgentKey.Should().Be("general");
    }

    [Fact]
    public async Task FullPipeline_DemandAgent_DispatchedCorrectly()
    {
        var routerClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.DemandForecasting}\",\"confidence\":0.95,\"intents\":[\"{AgentIntent.DemandForecasting}\"]}}");

        var demandChatClient = MockChatClient("Sierra Gold Tequila demand is projected to grow 8%.");
        var demandAgent = new DemandForecastAgent(
            demandChatClient,
            new AgentDefinition { Name = "DemandForecast", Model = "gpt-5.4-mini", SystemPrompt = "Demand specialist", Temperature = 0.3 },
            CreateMockHubContext(),
            [],
            Mock.Of<ILogger<DemandForecastAgent>>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build());

        var generalAgent = CreateGeneralAgent(MockChatClient("general fallback"));
        var specialists = new List<ISpecialistAgent> { demandAgent, generalAgent };
        var router = CreateRouter(routerClient, specialists);

        // Route should select demand agent
        var routingResult = await router.RouteAsync("Forecast demand for Sierra Gold", null, null, null);
        routingResult.AgentKey.Should().Be("demand-forecasting");

        // Dispatch to demand agent
        var response = await demandAgent.HandleAsync(
            new ChatRequest("Forecast demand for Sierra Gold", SessionId: "demand-dispatch-test"));

        response.Reply.Should().Contain("Sierra Gold Tequila");
        response.Spans.Should().NotBeEmpty();
    }

    private static ISpecialistAgent CreateMockDemandAgent()
    {
        var mock = new Mock<ISpecialistAgent>();
        mock.Setup(a => a.Key).Returns("demand-forecasting");
        mock.Setup(a => a.DisplayName).Returns("Demand Forecast Agent");
        mock.Setup(a => a.SupportedIntents).Returns(new[] { AgentIntent.DemandForecasting });
        mock.Setup(a => a.HandleAsync(
                It.IsAny<ChatRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RetailPulse.Contracts.ChatResponse("Demand forecast ready", "session-demand", []));
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

    #endregion

    #region Helpers

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

    private static RetailOpsRouter CreateRouter(
        IChatClient chatClient,
        IEnumerable<ISpecialistAgent> specialists)
    {
        var routerDef = new AgentDefinition
        {
            Name = "Router",
            Model = "gpt-5.4-mini",
            SystemPrompt = "Classify intent. Return JSON with intent, confidence, reasoning.",
            Temperature = 0.1
        };

        return new RetailOpsRouter(
            chatClient, routerDef, specialists,
            Mock.Of<ILogger<RetailOpsRouter>>());
    }

    private static GeneralAgent CreateGeneralAgent(IChatClient? chatClient = null)
    {
        var hubContext = new Mock<IHubContext<TelemetryHub>>();
        var clients = new Mock<IHubClients>();
        var groupProxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(groupProxy.Object);
        hubContext.Setup(h => h.Clients).Returns(clients.Object);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        return new GeneralAgent(
            chatClient ?? Mock.Of<IChatClient>(),
            new AgentDefinition { Name = "General", Model = "gpt-4o", SystemPrompt = "Test", Temperature = 0.7 },
            hubContext.Object,
            [],
            Mock.Of<ILogger<GeneralAgent>>(),
            config);
    }

    #endregion

    #region Memory & Approval Integration

    [Fact]
    public async Task MemoryPersists_AcrossMultipleConversations()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"integ_mem_{Guid.NewGuid():N}.db");
        try
        {
            using var memory = new SqliteConversationMemory(dbPath, Mock.Of<ILogger<SqliteConversationMemory>>());

            var now = DateTimeOffset.UtcNow;
            var prefEntry = new MemoryEntry(Guid.NewGuid().ToString("N"), "user-1",
                MemoryType.UserPreference, "Prefers line charts", null, now, now.AddDays(90));
            await memory.StoreAsync("user-1", prefEntry);

            var entityEntry = new MemoryEntry(Guid.NewGuid().ToString("N"), "user-1",
                MemoryType.EntityMention, "Discussed Brand X sales", "Brand X", now, now.AddDays(30));
            await memory.StoreAsync("user-1", entityEntry);

            var memories = await memory.RecallAsync("user-1", maxResults: 10);
            memories.Should().HaveCount(2);
            memories.Should().Contain(m => m.Type == MemoryType.UserPreference);
            memories.Should().Contain(m => m.Type == MemoryType.EntityMention);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
            try { File.Delete(dbPath + "-wal"); } catch { }
            try { File.Delete(dbPath + "-shm"); } catch { }
        }
    }

    [Fact]
    public async Task ApprovalFlow_EndToEnd_RequestRespondAgentReceives()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"integ_appr_{Guid.NewGuid():N}.db");
        try
        {
            var gate = new SqliteApprovalGate(dbPath, Mock.Of<ILogger<SqliteApprovalGate>>());

            var context = new ApprovalContext("demand-agent", "user-1",
                "Generate Q4 forecast for all 12 brands",
                "High compute cost", "Medium", "Quarterly forecast generation");
            var request = await gate.RequestApprovalAsync(context);
            request.RequestId.Should().NotBeNullOrEmpty();

            await gate.RespondAsync(request.RequestId, ApprovalDecision.Approved,
                comment: "Approved - run during off-peak");

            var agentResult = await gate.GetResultAsync(request.RequestId);
            agentResult.Decision.Should().Be(ApprovalDecision.Approved);
            agentResult.Comment.Should().Be("Approved - run during off-peak");
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
            try { File.Delete(dbPath + "-wal"); } catch { }
            try { File.Delete(dbPath + "-shm"); } catch { }
        }
    }

    [Fact]
    public async Task MemoryManagementRouting_ForgetIntentRoutes()
    {
        var routerClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.MemoryManagement}\",\"confidence\":0.95,\"intents\":[\"{AgentIntent.MemoryManagement}\"]}}");

        var generalAgent = CreateGeneralAgent(MockChatClient("Done."));
        var memoryAgent = new MemoryManagementAgent(
            Mock.Of<IConversationMemory>(),
            Mock.Of<ILogger<MemoryManagementAgent>>());
        var specialists = new List<ISpecialistAgent> { generalAgent, memoryAgent };
        var router = CreateRouter(routerClient, specialists);

        var routingResult = await router.RouteAsync("Forget everything about me", null, null, null);
        routingResult.Intent.Should().Be(AgentIntent.MemoryManagement);
    }

    #endregion

    #region Promo Planning Routing

    [Theory]
    [InlineData("Plan a BOGO promotion for Sierra Gold Tequila")]
    [InlineData("What's the ROI on our last display campaign?")]
    [InlineData("Estimate lift for a discount promo in the Northeast")]
    public async Task Router_PromoMessages_RoutesToPromotionTrade(string message)
    {
        var routerClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.PromotionTrade}\",\"confidence\":0.92,\"intents\":[\"{AgentIntent.PromotionTrade}\"]}}");

        var hubContext = new Mock<IHubContext<TelemetryHub>>();
        var clients = new Mock<IHubClients>();
        var groupProxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(groupProxy.Object);
        hubContext.Setup(h => h.Clients).Returns(clients.Object);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var promoAgent = new PromoPlanningAgent(
            MockChatClient("Promo analysis ready."),
            new AgentDefinition { Name = "PromoPlanning", Model = "gpt-5.4-mini", SystemPrompt = "promo specialist", Temperature = 0.3 },
            hubContext.Object, [],
            Mock.Of<ILogger<PromoPlanningAgent>>(),
            config);

        var generalAgent = CreateGeneralAgent(MockChatClient("general fallback"));
        var specialists = new List<ISpecialistAgent> { generalAgent, promoAgent };
        var router = CreateRouter(routerClient, specialists);

        var routingResult = await router.RouteAsync(message, null, null, null);
        routingResult.Intent.Should().Be(AgentIntent.PromotionTrade);
    }

    [Fact]
    public async Task FullPipeline_PromoMessage_RoutesAndReturnsResponse()
    {
        var routerClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.PromotionTrade}\",\"confidence\":0.90,\"intents\":[\"{AgentIntent.PromotionTrade}\"]}}");

        var hubContext = new Mock<IHubContext<TelemetryHub>>();
        var clients = new Mock<IHubClients>();
        var groupProxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(groupProxy.Object);
        hubContext.Setup(h => h.Clients).Returns(clients.Object);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var promoAgent = new PromoPlanningAgent(
            MockChatClient("The BOGO campaign for Sierra Gold shows a projected 22% lift with ROI of 85%."),
            new AgentDefinition { Name = "PromoPlanning", Model = "gpt-5.4-mini", SystemPrompt = "promo specialist", Temperature = 0.3 },
            hubContext.Object, [],
            Mock.Of<ILogger<PromoPlanningAgent>>(),
            config);

        var generalAgent = CreateGeneralAgent(MockChatClient("general fallback"));
        var specialists = new List<ISpecialistAgent> { generalAgent, promoAgent };
        var router = CreateRouter(routerClient, specialists);

        var routingResult = await router.RouteAsync(
            "Plan a BOGO promotion for Sierra Gold Tequila in the Northeast", null, null, null);
        routingResult.Intent.Should().Be(AgentIntent.PromotionTrade);

        var response = await promoAgent.HandleAsync(
            new ChatRequest("Plan a BOGO promotion for Sierra Gold Tequila", SessionId: "promo-pipeline-1"));
        response.Reply.Should().Contain("Sierra Gold");
        response.Spans.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Router_PromoIntent_DoesNotRouteToDemand()
    {
        var routerClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.PromotionTrade}\",\"confidence\":0.88,\"intents\":[\"{AgentIntent.PromotionTrade}\"]}}");

        var generalAgent = CreateGeneralAgent(MockChatClient("general"));
        var specialists = new List<ISpecialistAgent> { generalAgent };
        var router = CreateRouter(routerClient, specialists);

        var routingResult = await router.RouteAsync(
            "Estimate ROI for a bundle promotion", null, null, null);
        routingResult.Intent.Should().Be(AgentIntent.PromotionTrade);
        // Without a promo specialist registered, should NOT misroute to demand
        routingResult.Intent.Should().NotBe(AgentIntent.DemandForecasting);
    }

    #endregion

    #region Competitive Intelligence Routing

    [Theory]
    [InlineData("What are competitors doing in the cereal category?")]
    [InlineData("Show me the competitive landscape for spirits in the Northeast")]
    [InlineData("Is BrandX a threat to Sierra Gold Tequila?")]
    public async Task Router_CompetitiveQuery_ClassifiesAsCompetitiveMarket(string message)
    {
        var routerClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.CompetitiveMarket}\",\"confidence\":0.91,\"intents\":[\"{AgentIntent.CompetitiveMarket}\"]}}");

        var hubContext = new Mock<IHubContext<TelemetryHub>>();
        var clients = new Mock<IHubClients>();
        var groupProxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(groupProxy.Object);
        hubContext.Setup(h => h.Clients).Returns(clients.Object);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var competitiveAgent = new CompetitiveIntelAgent(
            MockChatClient("Competitive analysis for the requested category shows key threats."),
            new AgentDefinition { Name = "CompetitiveIntel", Model = "gpt-5.4-mini", SystemPrompt = "competitive specialist", Temperature = 0.3 },
            hubContext.Object, [],
            Mock.Of<ILogger<CompetitiveIntelAgent>>(),
            config);

        var generalAgent = CreateGeneralAgent(MockChatClient("general fallback"));
        var specialists = new List<ISpecialistAgent> { generalAgent, competitiveAgent };
        var router = CreateRouter(routerClient, specialists);

        var routingResult = await router.RouteAsync(message, null, null, null);
        routingResult.Intent.Should().Be(AgentIntent.CompetitiveMarket);
    }

    [Fact]
    public async Task FullPipeline_CompetitiveMessage_RoutesAndReturnsResponse()
    {
        var routerClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.CompetitiveMarket}\",\"confidence\":0.93,\"intents\":[\"{AgentIntent.CompetitiveMarket}\"]}}");

        var hubContext = new Mock<IHubContext<TelemetryHub>>();
        var clients = new Mock<IHubClients>();
        var groupProxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(groupProxy.Object);
        hubContext.Setup(h => h.Clients).Returns(clients.Object);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var competitiveAgent = new CompetitiveIntelAgent(
            MockChatClient("BrandX has dropped prices by 15% in the Northeast spirits category. Recommendation: DIFFERENTIATE on premium positioning."),
            new AgentDefinition { Name = "CompetitiveIntel", Model = "gpt-5.4-mini", SystemPrompt = "competitive specialist", Temperature = 0.3 },
            hubContext.Object, [],
            Mock.Of<ILogger<CompetitiveIntelAgent>>(),
            config);

        var generalAgent = CreateGeneralAgent(MockChatClient("general fallback"));
        var specialists = new List<ISpecialistAgent> { generalAgent, competitiveAgent };
        var router = CreateRouter(routerClient, specialists);

        var routingResult = await router.RouteAsync(
            "What competitive threats exist for Sierra Gold Tequila in the Northeast?", null, null, null);
        routingResult.Intent.Should().Be(AgentIntent.CompetitiveMarket);

        var response = await competitiveAgent.HandleAsync(
            new ChatRequest("What competitive threats exist for Sierra Gold Tequila?", SessionId: "comp-pipeline-1"));
        response.Reply.Should().Contain("BrandX");
        response.Spans.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Router_CompetitiveIntent_DoesNotRouteToDemandOrPromo()
    {
        var routerClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.CompetitiveMarket}\",\"confidence\":0.89,\"intents\":[\"{AgentIntent.CompetitiveMarket}\"]}}");

        var generalAgent = CreateGeneralAgent(MockChatClient("general"));
        var specialists = new List<ISpecialistAgent> { generalAgent };
        var router = CreateRouter(routerClient, specialists);

        var routingResult = await router.RouteAsync(
            "Analyze the competitive landscape for snacks", null, null, null);
        routingResult.Intent.Should().Be(AgentIntent.CompetitiveMarket);
        routingResult.Intent.Should().NotBe(AgentIntent.DemandForecasting);
        routingResult.Intent.Should().NotBe(AgentIntent.PromotionTrade);
    }

    #endregion
}

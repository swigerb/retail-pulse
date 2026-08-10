using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Agents.Routing;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Tests.Agents.Router;

/// <summary>
/// Tests for RetailOpsRouter — the LLM-based intent classifier that
/// routes messages to specialist agents via RoutingDecision.
/// </summary>
public class RetailOpsRouterTests
{
    #region Intent Classification — Happy Paths

    [Theory]
    [InlineData("What's the demand forecast for Brand X?", AgentIntent.DemandForecasting)]
    [InlineData("How did our last promotion perform?", AgentIntent.PromotionTrade)]
    [InlineData("Where are my shipments?", AgentIntent.SupplyShipments)]
    [InlineData("What are competitors doing?", AgentIntent.CompetitiveMarket)]
    [InlineData("What's distributor sentiment?", AgentIntent.SentimentField)]
    [InlineData("Show me the portfolio overview", AgentIntent.General)]
    public async Task RouteAsync_ClassifiesKnownIntents(string message, string expectedIntent)
    {
        IChatClient chatClient = MockChatClient(
            $"{{\"intent\":\"{expectedIntent}\",\"confidence\":0.92,\"intents\":[\"{expectedIntent}\"]}}");
        List<ISpecialistAgent> specialists = CreateSpecialists();
        RetailOpsRouter router = CreateRouter(chatClient, specialists);

        RoutingDecision result = await router.RouteAsync(message, null, null, null);

        result.Intent.Should().Be(expectedIntent);
        result.Confidence.Should().BeGreaterThanOrEqualTo(0.6);
    }

    [Fact]
    public async Task RouteAsync_DemandForecastMessage_RoutesToCorrectAgent()
    {
        IChatClient chatClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.DemandForecasting}\",\"confidence\":0.95,\"intents\":[\"{AgentIntent.DemandForecasting}\"]}}");
        List<ISpecialistAgent> specialists = CreateSpecialists();
        RetailOpsRouter router = CreateRouter(chatClient, specialists);

        RoutingDecision result = await router.RouteAsync(
            "What's the demand forecast for Brand X?", null, null, null);

        result.AgentKey.Should().Be("general");
        result.Intent.Should().Be(AgentIntent.DemandForecasting);
        result.Confidence.Should().BeGreaterThan(0.9);
    }

    [Fact]
    public async Task RouteAsync_PromotionMessage_ClassifiesCorrectly()
    {
        IChatClient chatClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.PromotionTrade}\",\"confidence\":0.88,\"intents\":[\"{AgentIntent.PromotionTrade}\"]}}");
        List<ISpecialistAgent> specialists = CreateSpecialists();
        RetailOpsRouter router = CreateRouter(chatClient, specialists);

        RoutingDecision result = await router.RouteAsync(
            "How did our last promotion perform?", null, null, null);

        result.Intent.Should().Be(AgentIntent.PromotionTrade);
    }

    [Fact]
    public async Task RouteAsync_SupplyChainMessage_ClassifiesCorrectly()
    {
        IChatClient chatClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.SupplyShipments}\",\"confidence\":0.91,\"intents\":[\"{AgentIntent.SupplyShipments}\"]}}");
        List<ISpecialistAgent> specialists = CreateSpecialists();
        RetailOpsRouter router = CreateRouter(chatClient, specialists);

        RoutingDecision result = await router.RouteAsync(
            "Where are my shipments?", null, null, null);

        result.Intent.Should().Be(AgentIntent.SupplyShipments);
    }

    [Fact]
    public async Task RouteAsync_CompetitiveMessage_ClassifiesCorrectly()
    {
        IChatClient chatClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.CompetitiveMarket}\",\"confidence\":0.85,\"intents\":[\"{AgentIntent.CompetitiveMarket}\"]}}");
        List<ISpecialistAgent> specialists = CreateSpecialists();
        RetailOpsRouter router = CreateRouter(chatClient, specialists);

        RoutingDecision result = await router.RouteAsync(
            "What are competitors doing?", null, null, null);

        result.Intent.Should().Be(AgentIntent.CompetitiveMarket);
    }

    [Fact]
    public async Task RouteAsync_SentimentMessage_ClassifiesCorrectly()
    {
        IChatClient chatClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.SentimentField}\",\"confidence\":0.87,\"intents\":[\"{AgentIntent.SentimentField}\"]}}");
        List<ISpecialistAgent> specialists = CreateSpecialists();
        RetailOpsRouter router = CreateRouter(chatClient, specialists);

        RoutingDecision result = await router.RouteAsync(
            "What's distributor sentiment?", null, null, null);

        result.Intent.Should().Be(AgentIntent.SentimentField);
    }

    #endregion

    #region Confidence Threshold / Fallback

    [Fact]
    public async Task RouteAsync_LowConfidence_FallsBackToGeneral()
    {
        IChatClient chatClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.DemandForecasting}\",\"confidence\":0.3,\"intents\":[\"{AgentIntent.DemandForecasting}\"]}}");
        List<ISpecialistAgent> specialists = CreateSpecialists();
        RetailOpsRouter router = CreateRouter(chatClient, specialists);

        RoutingDecision result = await router.RouteAsync(
            "Tell me something about stuff", null, null, null);

        // Threshold is 0.6 — should fall back
        result.Intent.Should().Be(AgentIntent.General);
    }

    [Fact]
    public async Task RouteAsync_ExactlyAtThreshold_DoesNotFallBack()
    {
        IChatClient chatClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.SupplyShipments}\",\"confidence\":0.6,\"intents\":[\"{AgentIntent.SupplyShipments}\"]}}");
        List<ISpecialistAgent> specialists = CreateSpecialists();
        RetailOpsRouter router = CreateRouter(chatClient, specialists);

        RoutingDecision result = await router.RouteAsync("shipments?", null, null, null);

        result.Intent.Should().Be(AgentIntent.SupplyShipments);
    }

    [Fact]
    public async Task RouteAsync_JustBelowThreshold_FallsBackToGeneral()
    {
        IChatClient chatClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.SupplyShipments}\",\"confidence\":0.59,\"intents\":[\"{AgentIntent.SupplyShipments}\"]}}");
        List<ISpecialistAgent> specialists = CreateSpecialists();
        RetailOpsRouter router = CreateRouter(chatClient, specialists);

        RoutingDecision result = await router.RouteAsync(
            "Maybe something about shipments?", null, null, null);

        result.Intent.Should().Be(AgentIntent.General);
    }

    [Fact]
    public async Task RouteAsync_ZeroConfidence_FallsBackToGeneral()
    {
        IChatClient chatClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.CompetitiveMarket}\",\"confidence\":0.0,\"intents\":[\"{AgentIntent.CompetitiveMarket}\"]}}");
        List<ISpecialistAgent> specialists = CreateSpecialists();
        RetailOpsRouter router = CreateRouter(chatClient, specialists);

        RoutingDecision result = await router.RouteAsync("...", null, null, null);

        result.Intent.Should().Be(AgentIntent.General);
    }

    #endregion

    #region Multi-Intent Messages

    [Fact]
    public async Task RouteAsync_MultiIntentResponse_ReturnsAllDetectedIntents()
    {
        IChatClient chatClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.DemandForecasting}\",\"confidence\":0.85,\"intents\":[\"{AgentIntent.DemandForecasting}\",\"{AgentIntent.CompetitiveMarket}\"]}}");
        List<ISpecialistAgent> specialists = CreateSpecialists();
        RetailOpsRouter router = CreateRouter(chatClient, specialists);

        RoutingDecision result = await router.RouteAsync(
            "How's demand and what are competitors doing?", null, null, null);

        result.Intent.Should().Be(AgentIntent.DemandForecasting);
        result.DetectedIntents.Should().HaveCountGreaterThan(1);
        result.DetectedIntents.Should().Contain(AgentIntent.CompetitiveMarket);
    }

    #endregion

    #region Conversation History / Context Propagation

    [Fact]
    public async Task RouteAsync_WithConversationHistory_PassesContextToLlm()
    {
        List<ChatMessage>? captured = null;
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((msgs, _, _) =>
                captured = [.. msgs])
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant,
                    $"{{\"intent\":\"{AgentIntent.General}\",\"confidence\":0.8}}")));

        List<ISpecialistAgent> specialists = CreateSpecialists();
        RetailOpsRouter router = CreateRouter(mockClient.Object, specialists);

        var history = new List<ChatHistoryMessage>
        {
            new("user", "Previous question"),
            new("assistant", "Previous answer")
        };

        await router.RouteAsync("Follow up question", history, null, null);

        captured.Should().NotBeNull();
        // System prompt + 2 history + current message = 4
        captured.Count.Should().BeGreaterThanOrEqualTo(4);
        captured.Last().Text.Should().Be("Follow up question");
    }

    [Fact]
    public async Task RouteAsync_NullHistory_DoesNotThrow()
    {
        IChatClient chatClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.General}\",\"confidence\":0.8}}");
        List<ISpecialistAgent> specialists = CreateSpecialists();
        RetailOpsRouter router = CreateRouter(chatClient, specialists);

        Func<Task<RoutingDecision>> act = () => router.RouteAsync("Hello", null, null, null);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RouteAsync_WithUserContext_CompletesSuccessfully()
    {
        IChatClient chatClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.General}\",\"confidence\":0.85}}");
        List<ISpecialistAgent> specialists = CreateSpecialists();
        RetailOpsRouter router = CreateRouter(chatClient, specialists);

        var user = new UserContext("obj-123", "Jane Smith", "jane@contoso.com");
        RoutingDecision result = await router.RouteAsync("hello", null, user, "tenant-contoso");

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task RouteAsync_WithTenantId_CompletesSuccessfully()
    {
        IChatClient chatClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.General}\",\"confidence\":0.85}}");
        List<ISpecialistAgent> specialists = CreateSpecialists();
        RetailOpsRouter router = CreateRouter(chatClient, specialists);

        RoutingDecision result = await router.RouteAsync("hello", null, null, "tenant-contoso");

        result.Should().NotBeNull();
    }

    #endregion

    #region Error Handling

    [Fact]
    public async Task RouteAsync_WhenLlmThrows_FallsBackToGeneral()
    {
        var chatClient = new Mock<IChatClient>();
        chatClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("LLM unavailable"));

        List<ISpecialistAgent> specialists = CreateSpecialists();
        RetailOpsRouter router = CreateRouter(chatClient.Object, specialists);

        RoutingDecision result = await router.RouteAsync("What's demand?", null, null, null);

        result.Intent.Should().Be(AgentIntent.General);
        result.Confidence.Should().Be(0.0);
    }

    [Fact]
    public async Task RouteAsync_MalformedJsonFromLlm_DoesNotThrow()
    {
        IChatClient chatClient = MockChatClient("This is not valid JSON at all");
        List<ISpecialistAgent> specialists = CreateSpecialists();
        RetailOpsRouter router = CreateRouter(chatClient, specialists);

        RoutingDecision result = await router.RouteAsync("demand forecast?", null, null, null);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task RouteAsync_EmptyJsonResponse_FallsBackToGeneral()
    {
        IChatClient chatClient = MockChatClient("{}");
        List<ISpecialistAgent> specialists = CreateSpecialists();
        RetailOpsRouter router = CreateRouter(chatClient, specialists);

        RoutingDecision result = await router.RouteAsync("test", null, null, null);

        result.Intent.Should().Be(AgentIntent.General);
    }

    [Fact]
    public async Task RouteAsync_WhenCancelled_FallsBackToGeneral()
    {
        var chatClient = new Mock<IChatClient>();
        chatClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        List<ISpecialistAgent> specialists = CreateSpecialists();
        RetailOpsRouter router = CreateRouter(chatClient.Object, specialists);

        RoutingDecision result = await router.RouteAsync("test", null, null, null);
        result.Intent.Should().Be(AgentIntent.General);
    }

    #endregion

    #region ParseClassification — Internal Method

    [Fact]
    public void ParseClassification_ValidJson_ReturnsCorrectIntentAndConfidence()
    {
        RetailOpsRouter.IntentClassification result = RetailOpsRouter.ParseClassification(
            $"{{\"intent\":\"{AgentIntent.DemandForecasting}\",\"confidence\":0.95,\"intents\":[\"{AgentIntent.DemandForecasting}\"]}}");

        result.Intent.Should().Be(AgentIntent.DemandForecasting);
        result.Confidence.Should().Be(0.95);
        result.DetectedIntents.Should().Contain(AgentIntent.DemandForecasting);
    }

    [Fact]
    public void ParseClassification_MissingIntent_DefaultsToGeneral()
    {
        RetailOpsRouter.IntentClassification result = RetailOpsRouter.ParseClassification(/*lang=json,strict*/ "{\"confidence\":0.8}");

        result.Intent.Should().Be(AgentIntent.General);
    }

    [Fact]
    public void ParseClassification_MissingConfidence_DefaultsToHalf()
    {
        RetailOpsRouter.IntentClassification result = RetailOpsRouter.ParseClassification(
            $"{{\"intent\":\"{AgentIntent.DemandForecasting}\"}}");

        result.Confidence.Should().Be(0.5);
    }

    [Fact]
    public void ParseClassification_UnknownIntent_NormalizesToGeneral()
    {
        RetailOpsRouter.IntentClassification result = RetailOpsRouter.ParseClassification(
                                 /*lang=json,strict*/
                                 "{\"intent\":\"unknown/category\",\"confidence\":0.9}");

        result.Intent.Should().Be(AgentIntent.General);
    }

    [Fact]
    public void ParseClassification_InvalidJson_ReturnsGeneralWithZeroConfidence()
    {
        RetailOpsRouter.IntentClassification result = RetailOpsRouter.ParseClassification("not json");

        result.Intent.Should().Be(AgentIntent.General);
        result.Confidence.Should().Be(0.0);
    }

    [Fact]
    public void ParseClassification_EmptyJson_ReturnsGeneralDefault()
    {
        RetailOpsRouter.IntentClassification result = RetailOpsRouter.ParseClassification("{}");

        result.Intent.Should().Be(AgentIntent.General);
        result.Confidence.Should().Be(0.5);
    }

    [Fact]
    public void ParseClassification_MultipleIntents_PreservesAll()
    {
        RetailOpsRouter.IntentClassification result = RetailOpsRouter.ParseClassification(
            $"{{\"intent\":\"{AgentIntent.DemandForecasting}\",\"confidence\":0.88,\"intents\":[\"{AgentIntent.DemandForecasting}\",\"{AgentIntent.CompetitiveMarket}\"]}}");

        result.DetectedIntents.Should().HaveCount(2);
        result.DetectedIntents.Should().Contain(AgentIntent.DemandForecasting);
        result.DetectedIntents.Should().Contain(AgentIntent.CompetitiveMarket);
    }

    [Fact]
    public void ParseClassification_EmptyIntentsArray_FallsBackToMainIntent()
    {
        RetailOpsRouter.IntentClassification result = RetailOpsRouter.ParseClassification(
            $"{{\"intent\":\"{AgentIntent.SupplyShipments}\",\"confidence\":0.7,\"intents\":[]}}");

        result.DetectedIntents.Should().HaveCount(1);
        result.DetectedIntents.Should().Contain(AgentIntent.SupplyShipments);
    }

    #endregion

    #region Keyword Fast-Path — Expanded Patterns

    [Theory]
    [InlineData("How is our portfolio performing?", AgentIntent.PortfolioHealth)]
    [InlineData("How is the overall performing?", AgentIntent.PortfolioHealth)]
    [InlineData("planogram optimization for dairy", AgentIntent.Planogram)]
    [InlineData("shelf space analysis for beverage aisle", AgentIntent.Planogram)]
    public async Task RouteAsync_KeywordFastPath_MatchesExpectedIntent(string message, string expectedIntent)
    {
        // LLM should NOT be called — keyword fast-path returns first.
        // Mock returns General; if fast-path fires, we get the real intent instead.
        IChatClient chatClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.General}\",\"confidence\":0.5}}");
        List<ISpecialistAgent> specialists = CreateSpecialistsWithAllIntents();
        RetailOpsRouter router = CreateRouter(chatClient, specialists);

        RoutingDecision result = await router.RouteAsync(message, null, null, null);

        result.Intent.Should().Be(expectedIntent);
        result.Confidence.Should().Be(0.95, "keyword fast-path assigns 0.95 confidence");
    }

    [Fact]
    public async Task RouteAsync_BrandRegionPerformanceQuery_DoesNotMatchPortfolioRegex()
    {
        // "How is Apex Grill performing in the Southwest?" should NOT hit PortfolioPerformingRegex.
        // It matches BrandPerformingRegex → General intent via keyword fast-path (no LLM call needed).
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant,
                    $"{{\"intent\":\"{AgentIntent.General}\",\"confidence\":0.88}}")));

        List<ISpecialistAgent> specialists = CreateSpecialistsWithAllIntents();
        RetailOpsRouter router = CreateRouter(mockClient.Object, specialists);

        RoutingDecision result = await router.RouteAsync(
            "How is Apex Grill performing in the Southwest this quarter?", null, null, null);

        result.Intent.Should().Be(AgentIntent.General);
        result.Confidence.Should().Be(0.95, "brand performing regex assigns keyword confidence");
        // Verify LLM was NOT called (keyword fast-path intercepted)
        mockClient.Verify(x => x.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("Show me the promotion ROI for Q4", AgentIntent.PromotionTrade)]
    [InlineData("promotion effectiveness this quarter", AgentIntent.PromotionTrade)]
    [InlineData("What's the brand scorecard?", AgentIntent.Scorecard)]
    public async Task RouteAsync_ExpandedKeywordPatterns_ClassifiesCorrectly(string message, string expectedIntent)
    {
        // These messages should route to the expected intent — either via keyword
        // fast-path (if expanded keywords are in place) or via LLM classification.
        IChatClient chatClient = MockChatClient(
            $"{{\"intent\":\"{expectedIntent}\",\"confidence\":0.90,\"intents\":[\"{expectedIntent}\"]}}");
        List<ISpecialistAgent> specialists = CreateSpecialistsWithAllIntents();
        RetailOpsRouter router = CreateRouter(chatClient, specialists);

        RoutingDecision result = await router.RouteAsync(message, null, null, null);

        result.Intent.Should().Be(expectedIntent);
        result.Confidence.Should().BeGreaterThanOrEqualTo(0.6);
    }

    [Theory]
    [InlineData("Forget everything about me")]
    [InlineData("Please clear my history")]
    [InlineData("Let's start fresh")]
    [InlineData("Reset my context before we continue")]
    [InlineData("Forget what I told you earlier")]
    public async Task RouteAsync_MemoryManagementDestructiveKeywords_HitKeywordFastPath(string message)
    {
        var mockClient = new Mock<IChatClient>();
        List<ISpecialistAgent> specialists = CreateSpecialistsWithMemoryIntent();
        RetailOpsRouter router = CreateRouter(mockClient.Object, specialists);

        RoutingDecision result = await router.RouteAsync(message, null, null, null);

        result.Intent.Should().Be(AgentIntent.MemoryManagement);
        result.Confidence.Should().Be(0.95);
        mockClient.Verify(x => x.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("Remember this: ClearDesk is trending up")]
    [InlineData("Remember that ClearDesk is trending up")]
    [InlineData("Remember that ClearDesk depletions are trending in the Northeast this quarter")]
    public async Task RouteAsync_RememberStoreCommands_HitMemoryManagementKeywordFastPath(string message)
    {
        var mockClient = new Mock<IChatClient>();
        List<ISpecialistAgent> specialists = CreateSpecialistsWithMemoryIntent();
        RetailOpsRouter router = CreateRouter(mockClient.Object, specialists);

        RoutingDecision result = await router.RouteAsync(message, null, null, null);

        result.Intent.Should().Be(AgentIntent.MemoryManagement);
        result.Confidence.Should().Be(0.95);
        mockClient.Verify(x => x.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RouteAsync_PortfolioPerformingQuery_HitsKeywordFastPath()
    {
        // "How is our portfolio performing?" should match PortfolioPerformingRegex → PortfolioHealth
        // without calling the LLM.
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant,
                    $"{{\"intent\":\"{AgentIntent.General}\",\"confidence\":0.5}}")));

        List<ISpecialistAgent> specialists = CreateSpecialistsWithAllIntents();
        RetailOpsRouter router = CreateRouter(mockClient.Object, specialists);

        RoutingDecision result = await router.RouteAsync(
            "How is our portfolio performing?", null, null, null);

        result.Intent.Should().Be(AgentIntent.PortfolioHealth);
        result.Confidence.Should().Be(0.95);
        // LLM should NOT have been called
        mockClient.Verify(x => x.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region Separate Router Model Configuration

    [Fact]
    public void RouterConstructor_AcceptsDistinctAgentDefinition()
    {
        // Verify the router can be constructed with a separate model/deployment
        // (simulating OpenAI:RouterDeployment config producing a distinct AgentDefinition).
        IChatClient routerClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.General}\",\"confidence\":0.8}}");
        List<ISpecialistAgent> specialists = CreateSpecialists();

        AgentDefinition routerDef = new()
        {
            Name = "Router",
            Model = "gpt-5.4-mini-router", // distinct deployment for router
            SystemPrompt = "Classify user intent. Return JSON.",
            Temperature = 0.0
        };

        RetailOpsRouter router = new(
            routerClient,
            routerDef,
            specialists,
            Mock.Of<ILogger<RetailOpsRouter>>());

        router.Should().NotBeNull();
    }

    [Fact]
    public async Task RouteAsync_WithDifferentRouterModel_UsesProvidedChatClient()
    {
        // When RouterDeployment is configured separately, the DI container provides
        // a distinct IChatClient to the router. Verify the router uses that client.
        var routerMock = new Mock<IChatClient>();
        routerMock
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant,
                    $"{{\"intent\":\"{AgentIntent.SupplyShipments}\",\"confidence\":0.91}}")));

        List<ISpecialistAgent> specialists = CreateSpecialistsWithAllIntents();

        AgentDefinition routerDef = new()
        {
            Name = "Router",
            Model = "gpt-5.4-mini-router",
            SystemPrompt = "Classify intent.",
            Temperature = 0.0
        };

        RetailOpsRouter router = new(
            routerMock.Object,
            routerDef,
            specialists,
            Mock.Of<ILogger<RetailOpsRouter>>());

        // Use a message that won't hit keyword fast-path
        RoutingDecision result = await router.RouteAsync(
            "Tell me about supply delays in the Midwest", null, null, null);

        result.Intent.Should().Be(AgentIntent.SupplyShipments);
        // Verify the router-specific client was called
        routerMock.Verify(x => x.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Explicit Chart-Intent Fast-Path

    [Theory]
    // Exact P0 gauge prompt: must reach the supply/inventory specialist (via the
    // "inventory" cue) and NOT the health council, despite the word "health".
    [InlineData("Show a gauge chart for Pinnacle Hardware inventory health in the Midwest", AgentIntent.SupplyShipments)]
    // Exact P0 bar prompt: depletion velocity → demand specialist.
    [InlineData("Show me a bar chart comparing depletion velocity for all spirits brands in the Northeast", AgentIntent.DemandForecasting)]
    // Generic, not brand-overfit: chart-type word + domain cue.
    [InlineData("Give me a line chart of gross margin by brand", AgentIntent.MarginAnalysis)]
    [InlineData("bar chart of planogram compliance by store", AgentIntent.Planogram)]
    [InlineData("Plot a gauge for stockout risk in the West", AgentIntent.SupplyShipments)]
    public async Task RouteAsync_ExplicitChartRequest_RoutesToChartCapableSpecialist(
        string message, string expectedIntent)
    {
        // Mock returns General/0.5 — if the chart fast-path did NOT fire, the result
        // would be General (below threshold), so a correct specialist intent proves
        // the deterministic detector intercepted before the LLM.
        IChatClient chatClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.General}\",\"confidence\":0.5}}");
        List<ISpecialistAgent> specialists = CreateSpecialistsWithAllIntents();
        RetailOpsRouter router = CreateRouter(chatClient, specialists);

        RoutingDecision result = await router.RouteAsync(message, null, null, null);

        result.Intent.Should().Be(expectedIntent);
        result.Confidence.Should().Be(0.95, "chart-intent fast-path assigns keyword confidence");
        result.DetectedIntents.Should().NotContain(AgentIntent.PortfolioHealth,
            "an explicit chart request must never route to the health council");
    }

    [Fact]
    public async Task RouteAsync_ExplicitGaugeChart_DoesNotInvokeLlmOrCouncil()
    {
        // The exact gauge prompt must be intercepted deterministically (no LLM call)
        // and must not be classified as council/health.
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant,
                    $"{{\"intent\":\"{AgentIntent.PortfolioHealth}\",\"confidence\":0.99}}")));

        List<ISpecialistAgent> specialists = CreateSpecialistsWithAllIntents();
        RetailOpsRouter router = CreateRouter(mockClient.Object, specialists);

        RoutingDecision result = await router.RouteAsync(
            "Show a gauge chart for Pinnacle Hardware inventory health in the Midwest", null, null, null);

        result.Intent.Should().Be(AgentIntent.SupplyShipments);
        result.DetectedIntents.Should().NotContain(AgentIntent.PortfolioHealth);
        mockClient.Verify(x => x.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    // Genuine council prompts WITHOUT an explicit chart request must still reach the council.
    [InlineData("How is our portfolio performing?", AgentIntent.PortfolioHealth)]
    [InlineData("How is the overall performing?", AgentIntent.PortfolioHealth)]
    public async Task RouteAsync_PortfolioHealthWithoutChart_StillRoutesToCouncil(
        string message, string expectedIntent)
    {
        IChatClient chatClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.General}\",\"confidence\":0.5}}");
        List<ISpecialistAgent> specialists = CreateSpecialistsWithAllIntents();
        RetailOpsRouter router = CreateRouter(chatClient, specialists);

        RoutingDecision result = await router.RouteAsync(message, null, null, null);

        result.Intent.Should().Be(expectedIntent);
        result.Confidence.Should().Be(0.95);
    }

    [Fact]
    public async Task RouteAsync_VerbGaugePortfolio_RoutesToCouncilNotChartSpecialist()
    {
        // "gauge" used as a VERB about portfolio performance must NOT trip the chart
        // fast-path. It should fall through to normal classification (here the LLM,
        // which classifies it as the portfolio-health council).
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant,
                    $"{{\"intent\":\"{AgentIntent.PortfolioHealth}\",\"confidence\":0.95}}")));

        List<ISpecialistAgent> specialists = CreateSpecialistsWithAllIntents();
        RetailOpsRouter router = CreateRouter(mockClient.Object, specialists);

        RoutingDecision result = await router.RouteAsync(
            "How would you gauge our portfolio's performance this quarter?", null, null, null);

        // Chart fast-path would have forced a chart specialist at confidence 0.95 with a
        // single detected intent; instead the LLM classifier decides → council.
        result.Intent.Should().Be(AgentIntent.PortfolioHealth);
        mockClient.Verify(x => x.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce(),
            "a verb use of 'gauge' must not be intercepted by the deterministic chart fast-path");
    }

    [Theory]
    // Chart-only type word used as a noun (no literal "chart") still fast-paths to a specialist.
    [InlineData("Show a gauge for Pinnacle Hardware inventory health in the Midwest", AgentIntent.SupplyShipments)]
    [InlineData("create a gauge for stockout risk in the West", AgentIntent.SupplyShipments)]
    public async Task RouteAsync_ChartOnlyTypeAsNoun_FastPathsToSpecialist(
        string message, string expectedIntent)
    {
        IChatClient chatClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.General}\",\"confidence\":0.5}}");
        List<ISpecialistAgent> specialists = CreateSpecialistsWithAllIntents();
        RetailOpsRouter router = CreateRouter(chatClient, specialists);

        RoutingDecision result = await router.RouteAsync(message, null, null, null);

        result.Intent.Should().Be(expectedIntent);
        result.Confidence.Should().Be(0.95);
        result.DetectedIntents.Should().NotContain(AgentIntent.PortfolioHealth);
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

    private static List<ISpecialistAgent> CreateSpecialists()
    {
        var general = new Mock<ISpecialistAgent>();
        general.Setup(s => s.Key).Returns("general");
        general.Setup(s => s.DisplayName).Returns("General Agent");
        general.Setup(s => s.SupportedIntents).Returns(
        [
            AgentIntent.General, AgentIntent.DemandForecasting,
            AgentIntent.PromotionTrade, AgentIntent.SupplyShipments,
            AgentIntent.CompetitiveMarket, AgentIntent.SentimentField
        ]);

        return [general.Object];
    }

    /// <summary>
    /// Creates specialists covering ALL known intents (including PortfolioHealth,
    /// Planogram, Scorecard, etc.) — needed by keyword fast-path tests that route
    /// to intents beyond what the basic CreateSpecialists() supports.
    /// </summary>
    private static List<ISpecialistAgent> CreateSpecialistsWithAllIntents()
    {
        var general = new Mock<ISpecialistAgent>();
        general.Setup(s => s.Key).Returns("general");
        general.Setup(s => s.DisplayName).Returns("General Agent");
        general.Setup(s => s.SupportedIntents).Returns(
        [
            AgentIntent.General, AgentIntent.DemandForecasting,
            AgentIntent.PromotionTrade, AgentIntent.SupplyShipments,
            AgentIntent.CompetitiveMarket, AgentIntent.SentimentField
        ]);

        var council = new Mock<ISpecialistAgent>();
        council.Setup(s => s.Key).Returns("council");
        council.Setup(s => s.DisplayName).Returns("Consensus Council");
        council.Setup(s => s.SupportedIntents).Returns([AgentIntent.PortfolioHealth]);

        var planogram = new Mock<ISpecialistAgent>();
        planogram.Setup(s => s.Key).Returns("planogram");
        planogram.Setup(s => s.DisplayName).Returns("Planogram Agent");
        planogram.Setup(s => s.SupportedIntents).Returns([AgentIntent.Planogram]);

        var scorecard = new Mock<ISpecialistAgent>();
        scorecard.Setup(s => s.Key).Returns("scorecard");
        scorecard.Setup(s => s.DisplayName).Returns("Scorecard Agent");
        scorecard.Setup(s => s.SupportedIntents).Returns([AgentIntent.Scorecard]);

        var storeOps = new Mock<ISpecialistAgent>();
        storeOps.Setup(s => s.Key).Returns("store-ops");
        storeOps.Setup(s => s.DisplayName).Returns("Store Ops Agent");
        storeOps.Setup(s => s.SupportedIntents).Returns([AgentIntent.StoreOps]);

        var margin = new Mock<ISpecialistAgent>();
        margin.Setup(s => s.Key).Returns("margin");
        margin.Setup(s => s.DisplayName).Returns("Margin Agent");
        margin.Setup(s => s.SupportedIntents).Returns([AgentIntent.MarginAnalysis]);

        return [general.Object, council.Object, planogram.Object, scorecard.Object, storeOps.Object, margin.Object];
    }

    private static List<ISpecialistAgent> CreateSpecialistsWithMemoryIntent()
    {
        List<ISpecialistAgent> specialists = CreateSpecialistsWithAllIntents();

        var memory = new Mock<ISpecialistAgent>();
        memory.Setup(s => s.Key).Returns("memory-management");
        memory.Setup(s => s.DisplayName).Returns("Memory Management");
        memory.Setup(s => s.SupportedIntents).Returns([AgentIntent.MemoryManagement]);

        specialists.Add(memory.Object);
        return specialists;
    }

    private static RetailOpsRouter CreateRouter(
        IChatClient chatClient,
        IEnumerable<ISpecialistAgent> specialists)
    {
        var routerDef = new AgentDefinition
        {
            Name = "Router",
            Model = "gpt-5.4-mini",
            SystemPrompt = "Classify user intent into retail categories. Return JSON with intent, confidence, and intents array.",
            Temperature = 0.1
        };

        return new RetailOpsRouter(
            chatClient,
            routerDef,
            specialists,
            Mock.Of<ILogger<RetailOpsRouter>>());
    }

    #endregion
}

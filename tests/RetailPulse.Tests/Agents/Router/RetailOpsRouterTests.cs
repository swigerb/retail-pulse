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

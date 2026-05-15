using System.ClientModel;
using System.ClientModel.Primitives;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Tests.Agents.Specialists;

/// <summary>
/// Tests for CompetitiveIntelAgent — the specialist agent handling competitive/market
/// intent classification. Verifies ISpecialistAgent contract compliance, response
/// shape, recommendation generation, error handling, and router integration.
/// Test-first: defines the expected contract before implementation exists.
/// </summary>
public class CompetitiveIntelAgentTests
{
    #region Identity & ISpecialistAgent Contract

    [Fact]
    public void Key_IsCompetitiveIntel()
    {
        var agent = CreateAgent();
        agent.Key.Should().Be("competitive-intel");
    }

    [Fact]
    public void DisplayName_IsNotEmpty()
    {
        var agent = CreateAgent();
        agent.DisplayName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SupportedIntents_ContainsCompetitiveMarket()
    {
        var agent = CreateAgent();
        agent.SupportedIntents.Should().Contain(AgentIntent.CompetitiveMarket);
    }

    [Fact]
    public void SupportedIntents_DoesNotContainGeneralFallback()
    {
        var agent = CreateAgent();
        agent.SupportedIntents.Should().NotContain(AgentIntent.General);
    }

    [Fact]
    public void SupportedIntents_DoesNotContainOtherSpecialistIntents()
    {
        var agent = CreateAgent();
        agent.SupportedIntents.Should().NotContain(AgentIntent.DemandForecasting);
        agent.SupportedIntents.Should().NotContain(AgentIntent.PromotionTrade);
        agent.SupportedIntents.Should().NotContain(AgentIntent.SupplyShipments);
        agent.SupportedIntents.Should().NotContain(AgentIntent.SentimentField);
    }

    [Fact]
    public void SupportedIntents_OnlyCompetitiveMarket()
    {
        var agent = CreateAgent();
        agent.SupportedIntents.Should().HaveCount(1);
        agent.SupportedIntents.Should().ContainSingle(i => i == AgentIntent.CompetitiveMarket);
    }

    #endregion

    #region HandleAsync — Response Shape

    [Fact]
    public async Task HandleAsync_ReturnsReplyFromModel()
    {
        var chatClient = MockChatClient("Competitor X has dropped prices by 15% in the Northeast on spirits.");
        var agent = CreateAgent(chatClient);

        var request = new ChatRequest("What are competitors doing in spirits?", SessionId: "session-comp-1");
        var response = await agent.HandleAsync(request);

        response.Reply.Should().Contain("Competitor");
        response.SessionId.Should().Be("session-comp-1");
    }

    [Fact]
    public async Task HandleAsync_GeneratesSessionIdWhenMissing()
    {
        var chatClient = MockChatClient("Competitive landscape analysis ready.");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(new ChatRequest("Competitive landscape"));

        response.SessionId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HandleAsync_IncludesSpans()
    {
        var chatClient = MockChatClient("Competitive analysis complete.");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("Show competitor pricing for cereal", SessionId: "span-test-comp"));

        response.Spans.Should().NotBeEmpty();
        response.Spans.Should().Contain(s => s.Type == "thought");
        response.Spans.Should().Contain(s => s.Type == "response");
    }

    [Fact]
    public async Task HandleAsync_PropagatesSessionId()
    {
        var chatClient = MockChatClient("ok");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("competitive?", SessionId: "my-comp-session-42"));

        response.SessionId.Should().Be("my-comp-session-42");
    }

    [Fact]
    public async Task HandleAsync_IncludesTotalDurationMs()
    {
        var chatClient = MockChatClient("done");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("competitor pricing?", SessionId: "dur-comp-test"));

        response.TotalDurationMs.Should().NotBeNull();
        response.TotalDurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task HandleAsync_SpansHaveCorrectSessionId()
    {
        var chatClient = MockChatClient("done");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("test", SessionId: "span-session-comp-test"));

        response.Spans.Should().OnlyContain(s => s.SessionId == "span-session-comp-test");
    }

    [Fact]
    public async Task HandleAsync_SpansHaveTimestamps()
    {
        var chatClient = MockChatClient("done");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("test", SessionId: "ts-comp-test"));

        response.Spans.Should().OnlyContain(s => s.Timestamp > DateTimeOffset.MinValue);
    }

    #endregion

    #region HandleAsync — Structured Competitive Assessment

    [Fact]
    public async Task HandleAsync_CompetitiveQuery_ReturnsStructuredAssessment()
    {
        var chatClient = MockChatClient(
            "## Competitive Assessment\n" +
            "**Threat Level:** High\n" +
            "**Competitor:** ValueBrand\n" +
            "**Action:** Price undercut by 12% in spirits/Northeast\n" +
            "**Recommendation:** MATCH — adjust pricing to maintain market share");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("What competitive threats exist for Sierra Gold Tequila?", SessionId: "assess-1"));

        response.Reply.Should().NotBeNullOrWhiteSpace();
        response.Reply.Should().Contain("Competitive");
    }

    [Fact]
    public async Task HandleAsync_MatchRecommendation_WhenPriceUndercutDetected()
    {
        var chatClient = MockChatClient(
            "Recommendation: MATCH — Competitor has undercut pricing by 15%. " +
            "Recommend matching their price point to prevent market share erosion.");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("Competitor X dropped prices. What should we do?", SessionId: "match-1"));

        response.Reply.Should().Contain("MATCH");
    }

    [Fact]
    public async Task HandleAsync_DifferentiateRecommendation_WhenQualityAdvantage()
    {
        var chatClient = MockChatClient(
            "Recommendation: DIFFERENTIATE — Our premium positioning and quality rating " +
            "justify our price premium. Focus marketing on quality differentiators.");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("Our brand is premium. Should we match competitor prices?", SessionId: "diff-1"));

        response.Reply.Should().Contain("DIFFERENTIATE");
    }

    [Fact]
    public async Task HandleAsync_IgnoreRecommendation_WhenThreatIsLow()
    {
        var chatClient = MockChatClient(
            "Recommendation: IGNORE — The competitor operates in a different segment " +
            "and their price change is unlikely to affect our market share.");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("A niche competitor changed prices. Relevant?", SessionId: "ignore-1"));

        response.Reply.Should().Contain("IGNORE");
    }

    [Fact]
    public async Task HandleAsync_PreemptRecommendation_WhenTrendDetected()
    {
        var chatClient = MockChatClient(
            "Recommendation: PREEMPT — Market trend data suggests competitors will " +
            "launch aggressive promotions next quarter. Recommend proactive campaign.");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("What competitive moves should we anticipate?", SessionId: "preempt-1"));

        response.Reply.Should().Contain("PREEMPT");
    }

    #endregion

    #region HandleAsync — History and Conversation Context

    [Fact]
    public async Task HandleAsync_WithHistory_PassesContextToLlm()
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
                new ChatMessage(ChatRole.Assistant, "done")));

        var agent = CreateAgent(mockClient.Object);
        var request = new ChatRequest(
            "Now show me threats for that region",
            SessionId: "hist-comp-1",
            History:
            [
                new ChatHistoryMessage("user", "Show competitor pricing for cereal in Northeast"),
                new ChatHistoryMessage("assistant", "Here's the competitor pricing data for cereal in Northeast...")
            ]);

        await agent.HandleAsync(request);

        captured.Should().NotBeNull();
        // System + history(2) + current = 4 messages
        captured.Should().HaveCount(4);
        captured.Select(m => m.Role).Should().ContainInOrder(
            ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User);
    }

    [Fact]
    public async Task HandleAsync_CapsHistoryAtTenTurns()
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
                new ChatMessage(ChatRole.Assistant, "done")));

        var history = Enumerable.Range(1, 22)
            .Select(i => new ChatHistoryMessage(i % 2 == 0 ? "assistant" : "user", $"h-{i}"))
            .ToList();

        var agent = CreateAgent(mockClient.Object);
        await agent.HandleAsync(
            new ChatRequest("current", SessionId: "cap-comp-test", History: history));

        captured.Should().NotBeNull();
        // System(1) + capped history(20) + current(1) = 22
        captured.Should().HaveCount(22);
    }

    #endregion

    #region Tool Isolation

    [Fact]
    public async Task HandleAsync_UsesProvidedToolsInChatOptions()
    {
        ChatOptions? capturedOptions = null;
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((_, opts, _) =>
                capturedOptions = opts)
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant, "done")));

        var compTools = new List<AITool>
        {
            AIFunctionFactory.Create(() => "pricing data", "get_competitor_pricing"),
            AIFunctionFactory.Create(() => "market share", "get_market_share"),
            AIFunctionFactory.Create(() => "threats", "detect_threats"),
            AIFunctionFactory.Create(() => "landscape", "get_competitive_landscape")
        };

        var agent = CreateAgent(mockClient.Object, compTools);
        await agent.HandleAsync(new ChatRequest("competitive?", SessionId: "tool-comp-test"));

        capturedOptions.Should().NotBeNull();
        capturedOptions.Tools.Should().HaveCount(4);
        capturedOptions.Tools.OfType<AIFunction>().Select(t => t.Name)
            .Should().Contain("get_competitor_pricing")
            .And.Contain("get_market_share")
            .And.Contain("detect_threats")
            .And.Contain("get_competitive_landscape");
    }

    [Fact]
    public void CompetitiveAgent_KeyDiffersFromGeneral()
    {
        var compAgent = CreateAgent();
        var generalAgent = Fixtures.AgentTestFixtures.CreateGeneralAgent();

        compAgent.Key.Should().NotBe(generalAgent.Key);
        compAgent.Key.Should().Be("competitive-intel");
        generalAgent.Key.Should().Be("general");
    }

    #endregion

    #region Unknown Competitor Handling

    [Fact]
    public async Task HandleAsync_UnknownCompetitor_ReturnsFriendlyMessage()
    {
        var chatClient = MockChatClient(
            "I don't have data for that competitor. The known competitors in the spirits " +
            "category include ValueBrand, PremiumCo, and BudgetSpirits.");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("What is NonExistentCompany doing?", SessionId: "unknown-comp"));

        response.Reply.Should().NotBeNullOrWhiteSpace();
        response.Reply.Should().NotContain("exception", because: "should be a friendly message, not a stack trace");
        response.SessionId.Should().Be("unknown-comp");
    }

    [Fact]
    public async Task HandleAsync_UnknownCategory_ReturnsFriendlyMessage()
    {
        var chatClient = MockChatClient(
            "I don't have competitive data for that category. Available categories include spirits, snacks, and beverages.");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("Competitive landscape for alien technology", SessionId: "unknown-cat"));

        response.Reply.Should().NotBeNullOrWhiteSpace();
        response.SessionId.Should().Be("unknown-cat");
    }

    #endregion

    #region Error Handling

    [Fact]
    public async Task HandleAsync_WhenRateLimited_ReturnsFriendlyMessage()
    {
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ClientResultException(
                "Too Many Requests",
                CreatePipelineResponse(429),
                null));

        var agent = CreateAgent(mockClient.Object);
        var response = await agent.HandleAsync(
            new ChatRequest("competitive?", SessionId: "s-429-comp"));

        response.Reply.Should().Contain("rate-limited");
        response.SessionId.Should().Be("s-429-comp");
        response.Spans.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenUnexpectedError_ReturnsFriendlyMessage()
    {
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var agent = CreateAgent(mockClient.Object);
        var response = await agent.HandleAsync(
            new ChatRequest("competitive?", SessionId: "s-err-comp"));

        response.Reply.Should().Contain("Something went wrong");
        response.TotalDurationMs.Should().NotBeNull();
    }

    #endregion

    #region Parameterized Brand Tests

    [Theory]
    [InlineData("Sierra Gold Tequila")]
    [InlineData("Ridgeline Bourbon")]
    [InlineData("Summit Vodka")]
    [InlineData("FreshMart")]
    [InlineData("Harvest Table")]
    [InlineData("Apex Grill")]
    [InlineData("Coastline Tacos")]
    [InlineData("Pinnacle Hardware")]
    [InlineData("Summit Outdoor")]
    [InlineData("ClearDesk")]
    [InlineData("Urban Living")]
    [InlineData("Foundry Home")]
    public async Task HandleAsync_AllTenantBrands_ProcessWithoutError(string brand)
    {
        var chatClient = MockChatClient($"Competitive analysis for {brand}: 3 active competitors identified.");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest($"What are competitors doing against {brand}?", SessionId: $"brand-comp-{brand}"));

        response.Reply.Should().NotBeNullOrWhiteSpace();
        response.SessionId.Should().NotBeNullOrWhiteSpace();
        response.Spans.Should().NotBeEmpty();
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

    private static CompetitiveIntelAgent CreateAgent(
        IChatClient? chatClient = null,
        IEnumerable<AITool>? tools = null)
    {
        var hubContext = CreateMockHubContext();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        var pipeline = new AgentExecutionPipeline(
            chatClient ?? Mock.Of<IChatClient>(),
            hubContext,
            config,
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());

        return new CompetitiveIntelAgent(
            pipeline,
            new AgentDefinition
            {
                Name = "CompetitiveIntel",
                Model = "gpt-5.4-mini",
                SystemPrompt = "You are a competitive intelligence specialist for retail brands.",
                Temperature = 0.3
            },
            tools ?? [],
            hubContext,
            Mock.Of<ILogger<CompetitiveIntelAgent>>());
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

    private static PipelineResponse CreatePipelineResponse(int status)
    {
        var response = new Mock<PipelineResponse>();
        response.SetupGet(x => x.Status).Returns(status);
        response.SetupGet(x => x.ReasonPhrase).Returns("Too Many Requests");
        response.SetupProperty(x => x.ContentStream, new MemoryStream());
        response.Setup(x => x.Dispose());
        return response.Object;
    }

    #endregion
}

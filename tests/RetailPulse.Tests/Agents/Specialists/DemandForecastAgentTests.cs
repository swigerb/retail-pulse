using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace RetailPulse.Tests.Agents.Specialists;

/// <summary>
/// Tests for DemandForecastAgent — the specialist agent handling demand/forecasting
/// intent classification. Verifies ISpecialistAgent contract compliance, response
/// shape, tool isolation, error handling, and router integration.
/// </summary>
public class DemandForecastAgentTests
{
    #region Identity & ISpecialistAgent Contract

    [Fact]
    public void Key_IsDemandForecasting()
    {
        var agent = CreateAgent();
        agent.Key.Should().Be("demand-forecasting");
    }

    [Fact]
    public void DisplayName_IsNotEmpty()
    {
        var agent = CreateAgent();
        agent.DisplayName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SupportedIntents_ContainsDemandForecasting()
    {
        var agent = CreateAgent();
        agent.SupportedIntents.Should().Contain(AgentIntent.DemandForecasting);
    }

    [Fact]
    public void SupportedIntents_DoesNotContainGeneralFallback()
    {
        // Demand agent is a specialist — should NOT claim general/fallback
        var agent = CreateAgent();
        agent.SupportedIntents.Should().NotContain(AgentIntent.General);
    }

    [Fact]
    public void SupportedIntents_DoesNotContainOtherSpecialistIntents()
    {
        var agent = CreateAgent();
        agent.SupportedIntents.Should().NotContain(AgentIntent.PromotionTrade);
        agent.SupportedIntents.Should().NotContain(AgentIntent.SupplyShipments);
        agent.SupportedIntents.Should().NotContain(AgentIntent.CompetitiveMarket);
        agent.SupportedIntents.Should().NotContain(AgentIntent.SentimentField);
    }

    [Fact]
    public void SupportedIntents_OnlyDemand()
    {
        var agent = CreateAgent();
        agent.SupportedIntents.Should().HaveCount(1);
        agent.SupportedIntents.Should().ContainSingle(i => i == AgentIntent.DemandForecasting);
    }

    #endregion

    #region HandleAsync — Response Shape

    [Fact]
    public async Task HandleAsync_ReturnsReplyFromModel()
    {
        var chatClient = MockChatClient("Sierra Gold Tequila demand is projected to grow 8% next quarter.");
        var agent = CreateAgent(chatClient);

        var request = new ChatRequest("What's the demand forecast for Sierra Gold Tequila?", SessionId: "session-demand-1");
        var response = await agent.HandleAsync(request);

        response.Reply.Should().Contain("Sierra Gold Tequila");
        response.SessionId.Should().Be("session-demand-1");
    }

    [Fact]
    public async Task HandleAsync_GeneratesSessionIdWhenMissing()
    {
        var chatClient = MockChatClient("Forecast ready");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(new ChatRequest("Forecast demand"));

        response.SessionId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HandleAsync_IncludesSpans()
    {
        var chatClient = MockChatClient("Forecast analysis complete");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("Generate forecast for Ridgeline Bourbon", SessionId: "span-test"));

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
            new ChatRequest("demand?", SessionId: "my-demand-session-42"));

        response.SessionId.Should().Be("my-demand-session-42");
    }

    [Fact]
    public async Task HandleAsync_IncludesTotalDurationMs()
    {
        var chatClient = MockChatClient("done");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("forecast?", SessionId: "dur-test"));

        response.TotalDurationMs.Should().NotBeNull();
        response.TotalDurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task HandleAsync_SpansHaveCorrectSessionId()
    {
        var chatClient = MockChatClient("done");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("test", SessionId: "span-session-test"));

        response.Spans.Should().OnlyContain(s => s.SessionId == "span-session-test");
    }

    [Fact]
    public async Task HandleAsync_SpansHaveTimestamps()
    {
        var chatClient = MockChatClient("done");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("test", SessionId: "ts-test"));

        response.Spans.Should().OnlyContain(s => s.Timestamp > DateTimeOffset.MinValue);
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
                captured = msgs.ToList())
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant, "done")));

        var agent = CreateAgent(mockClient.Object);
        var request = new ChatRequest(
            "Now forecast next quarter",
            SessionId: "hist-1",
            History:
            [
                new ChatHistoryMessage("user", "Show me Sierra Gold demand"),
                new ChatHistoryMessage("assistant", "Here's the historical demand for Sierra Gold...")
            ]);

        await agent.HandleAsync(request);

        captured.Should().NotBeNull();
        // System + history(2) + current = 4 messages
        captured!.Should().HaveCount(4);
        captured!.Select(m => m.Role).Should().ContainInOrder(
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
                captured = msgs.ToList())
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant, "done")));

        var history = Enumerable.Range(1, 22)
            .Select(i => new ChatHistoryMessage(i % 2 == 0 ? "assistant" : "user", $"h-{i}"))
            .ToList();

        var agent = CreateAgent(mockClient.Object);
        await agent.HandleAsync(
            new ChatRequest("current", SessionId: "cap-test", History: history));

        captured.Should().NotBeNull();
        // System(1) + capped history(20) + current(1) = 22
        captured!.Should().HaveCount(22);
    }

    #endregion

    #region Tool Isolation — Uses Own Tools, Not GeneralAgent's

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

        // Create with specific demand tools (2 fake tools)
        var demandTools = new List<AITool>
        {
            AIFunctionFactory.Create(() => "historical data", "GetHistoricalDemand"),
            AIFunctionFactory.Create(() => "forecast", "GenerateForecast")
        };

        var agent = CreateAgent(mockClient.Object, demandTools);
        await agent.HandleAsync(new ChatRequest("forecast?", SessionId: "tool-test"));

        capturedOptions.Should().NotBeNull();
        capturedOptions!.Tools.Should().HaveCount(2);
        capturedOptions.Tools!.OfType<AIFunction>().Select(t => t.Name)
            .Should().Contain("GetHistoricalDemand")
            .And.Contain("GenerateForecast");
    }

    [Fact]
    public void DemandAgent_KeyDiffersFromGeneral()
    {
        var demandAgent = CreateAgent();
        var generalAgent = Fixtures.AgentTestFixtures.CreateGeneralAgent();

        demandAgent.Key.Should().NotBe(generalAgent.Key);
        demandAgent.Key.Should().Be("demand-forecasting");
        generalAgent.Key.Should().Be("general");
    }

    #endregion

    #region Missing Brand Handling

    [Fact]
    public async Task HandleAsync_MissingBrand_ReturnsFriendlyMessage()
    {
        var chatClient = MockChatClient("I don't have data for that brand. The available brands are Sierra Gold Tequila, Ridgeline Bourbon, and Summit Vodka.");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("What's the demand for NonExistent Brand?", SessionId: "missing-brand"));

        response.Reply.Should().NotBeNullOrWhiteSpace();
        response.Reply.Should().NotContain("exception", because: "should be a friendly message, not a stack trace");
        response.SessionId.Should().Be("missing-brand");
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
            new ChatRequest("forecast?", SessionId: "s-429"));

        response.Reply.Should().Contain("rate-limited");
        response.SessionId.Should().Be("s-429");
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
            new ChatRequest("forecast?", SessionId: "s-err"));

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
        var chatClient = MockChatClient($"Forecast for {brand}: moderate growth expected.");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest($"What's the demand forecast for {brand}?", SessionId: $"brand-{brand}"));

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

    private static DemandForecastAgent CreateAgent(
        IChatClient? chatClient = null,
        IEnumerable<AITool>? tools = null)
    {
        var hubContext = CreateMockHubContext();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        return new DemandForecastAgent(
            chatClient ?? Mock.Of<IChatClient>(),
            new AgentDefinition
            {
                Name = "DemandForecast",
                Model = "gpt-5.4-mini",
                SystemPrompt = "You are a demand forecasting specialist for retail brands.",
                Temperature = 0.3
            },
            hubContext,
            tools ?? [],
            Mock.Of<ILogger<DemandForecastAgent>>(),
            config);
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

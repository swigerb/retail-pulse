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
using RetailPulse.Contracts.Approval;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Tests.Agents.Specialists;

/// <summary>
/// Tests for PromoPlanningAgent — the specialist agent handling promotion/trade
/// intent classification. Verifies ISpecialistAgent contract compliance, response
/// shape, tool isolation, approval gate, error handling, and router integration.
/// </summary>
public class PromoPlanningAgentTests
{
    #region Identity & ISpecialistAgent Contract

    [Fact]
    public void Key_IsPromoPlanning()
    {
        PromoPlanningAgent agent = CreateAgent();
        agent.Key.Should().Be("promo-planning");
    }

    [Fact]
    public void DisplayName_IsNotEmpty()
    {
        PromoPlanningAgent agent = CreateAgent();
        agent.DisplayName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SupportedIntents_ContainsPromotionTrade()
    {
        PromoPlanningAgent agent = CreateAgent();
        agent.SupportedIntents.Should().Contain(AgentIntent.PromotionTrade);
    }

    [Fact]
    public void SupportedIntents_DoesNotContainGeneralFallback()
    {
        // Promo agent is a specialist — should NOT claim general/fallback
        PromoPlanningAgent agent = CreateAgent();
        agent.SupportedIntents.Should().NotContain(AgentIntent.General);
    }

    [Fact]
    public void SupportedIntents_DoesNotContainOtherSpecialistIntents()
    {
        PromoPlanningAgent agent = CreateAgent();
        agent.SupportedIntents.Should().NotContain(AgentIntent.DemandForecasting);
        agent.SupportedIntents.Should().NotContain(AgentIntent.SupplyShipments);
        agent.SupportedIntents.Should().NotContain(AgentIntent.CompetitiveMarket);
        agent.SupportedIntents.Should().NotContain(AgentIntent.SentimentField);
    }

    [Fact]
    public void SupportedIntents_OnlyPromotionTrade()
    {
        PromoPlanningAgent agent = CreateAgent();
        agent.SupportedIntents.Should().HaveCount(1);
        agent.SupportedIntents.Should().ContainSingle(i => i == AgentIntent.PromotionTrade);
    }

    #endregion

    #region HandleAsync — Response Shape

    [Fact]
    public async Task HandleAsync_ReturnsReplyFromModel()
    {
        IChatClient chatClient = MockChatClient("Sierra Gold Tequila promo lift is projected at 12% for the spring campaign.");
        PromoPlanningAgent agent = CreateAgent(chatClient);

        var request = new ChatRequest("What's the promo plan for Sierra Gold Tequila?", SessionId: "session-promo-1");
        Contracts.ChatResponse response = await agent.HandleAsync(request);

        response.Reply.Should().Contain("Sierra Gold Tequila");
        response.SessionId.Should().Be("session-promo-1");
    }

    [Fact]
    public async Task HandleAsync_GeneratesSessionIdWhenMissing()
    {
        IChatClient chatClient = MockChatClient("Promotion plan ready");
        PromoPlanningAgent agent = CreateAgent(chatClient);

        Contracts.ChatResponse response = await agent.HandleAsync(new ChatRequest("Plan a promo"));

        response.SessionId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HandleAsync_IncludesSpans()
    {
        IChatClient chatClient = MockChatClient("Promotion analysis complete");
        PromoPlanningAgent agent = CreateAgent(chatClient);

        Contracts.ChatResponse response = await agent.HandleAsync(
            new ChatRequest("Generate promo plan for Ridgeline Bourbon", SessionId: "span-test"));

        response.Spans.Should().NotBeEmpty();
        response.Spans.Should().Contain(s => s.Type == "thought");
        response.Spans.Should().Contain(s => s.Type == "response");
    }

    [Fact]
    public async Task HandleAsync_PropagatesSessionId()
    {
        IChatClient chatClient = MockChatClient("ok");
        PromoPlanningAgent agent = CreateAgent(chatClient);

        Contracts.ChatResponse response = await agent.HandleAsync(
            new ChatRequest("promo?", SessionId: "my-promo-session-42"));

        response.SessionId.Should().Be("my-promo-session-42");
    }

    [Fact]
    public async Task HandleAsync_IncludesTotalDurationMs()
    {
        IChatClient chatClient = MockChatClient("done");
        PromoPlanningAgent agent = CreateAgent(chatClient);

        Contracts.ChatResponse response = await agent.HandleAsync(
            new ChatRequest("promo?", SessionId: "dur-test"));

        response.TotalDurationMs.Should().NotBeNull();
        response.TotalDurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task HandleAsync_SpansHaveTimestamps()
    {
        IChatClient chatClient = MockChatClient("done");
        PromoPlanningAgent agent = CreateAgent(chatClient);

        Contracts.ChatResponse response = await agent.HandleAsync(
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
                captured = [.. msgs])
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant, "done")));

        PromoPlanningAgent agent = CreateAgent(mockClient.Object);
        var request = new ChatRequest(
            "Now plan next quarter's promo",
            SessionId: "hist-1",
            History:
            [
                new ChatHistoryMessage("user", "Show me Sierra Gold promo history"),
                new ChatHistoryMessage("assistant", "Here's the historical promo data for Sierra Gold...")
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

        PromoPlanningAgent agent = CreateAgent(mockClient.Object);
        await agent.HandleAsync(
            new ChatRequest("current", SessionId: "cap-test", History: history));

        captured.Should().NotBeNull();
        // System(1) + capped history(20) + current(1) = 22
        captured.Should().HaveCount(22);
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

        // Create with specific promo tools (2 fake tools)
        var promoTools = new List<AITool>
        {
            AIFunctionFactory.Create(() => "promo history", "GetPromoHistory"),
            AIFunctionFactory.Create(() => "lift calc", "CalculatePromoLift")
        };

        PromoPlanningAgent agent = CreateAgent(mockClient.Object, promoTools);
        await agent.HandleAsync(new ChatRequest("promo?", SessionId: "tool-test"));

        capturedOptions.Should().NotBeNull();
        capturedOptions.Tools.Should().HaveCount(2);
        capturedOptions.Tools.OfType<AIFunction>().Select(t => t.Name)
            .Should().Contain("GetPromoHistory")
            .And.Contain("CalculatePromoLift");
    }

    [Fact]
    public void PromoAgent_KeyDiffersFromGeneral()
    {
        PromoPlanningAgent promoAgent = CreateAgent();
        GeneralAgent generalAgent = Fixtures.AgentTestFixtures.CreateGeneralAgent();

        promoAgent.Key.Should().NotBe(generalAgent.Key);
        promoAgent.Key.Should().Be("promo-planning");
        generalAgent.Key.Should().Be("general");
    }

    #endregion

    #region Approval Gate

    [Fact]
    public async Task CheckApprovalAsync_HighSpend_TriggersApproval()
    {
        Mock<IApprovalGate> mockGate = CreateMockApprovalGate();
        PromoPlanningAgent agent = CreateAgent(approvalGate: mockGate.Object);

        ApprovalResult? result = await agent.CheckApprovalAsync(
            spend: 600_000, roi: 15, userId: "user-1", description: "Summer campaign");

        result.Should().NotBeNull();
        result.Decision.Should().Be(ApprovalDecision.Approved);
        mockGate.Verify(g => g.RequestApprovalAsync(
            It.Is<ApprovalContext>(c => c.Urgency == "High"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckApprovalAsync_MediumSpendLowRoi_TriggersApproval()
    {
        Mock<IApprovalGate> mockGate = CreateMockApprovalGate();
        PromoPlanningAgent agent = CreateAgent(approvalGate: mockGate.Object);

        ApprovalResult? result = await agent.CheckApprovalAsync(
            spend: 150_000, roi: 5, userId: "user-2", description: "Low ROI promo");

        result.Should().NotBeNull();
        result.Decision.Should().Be(ApprovalDecision.Approved);
        mockGate.Verify(g => g.RequestApprovalAsync(
            It.Is<ApprovalContext>(c => c.Urgency == "Medium"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckApprovalAsync_NormalSpend_ReturnsNull()
    {
        Mock<IApprovalGate> mockGate = CreateMockApprovalGate();
        PromoPlanningAgent agent = CreateAgent(approvalGate: mockGate.Object);

        ApprovalResult? result = await agent.CheckApprovalAsync(
            spend: 50_000, roi: 20, userId: "user-3", description: "Small promo");

        result.Should().BeNull();
        mockGate.Verify(g => g.RequestApprovalAsync(
            It.IsAny<ApprovalContext>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckApprovalAsync_NoApprovalGate_ReturnsNull()
    {
        PromoPlanningAgent agent = CreateAgent(approvalGate: null);

        ApprovalResult? result = await agent.CheckApprovalAsync(
            spend: 1_000_000, roi: 5, userId: "user-4", description: "Huge campaign");

        result.Should().BeNull();
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

        PromoPlanningAgent agent = CreateAgent(mockClient.Object);
        Contracts.ChatResponse response = await agent.HandleAsync(
            new ChatRequest("promo?", SessionId: "s-429"));

        response.Reply.Should().Contain("high demand");
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

        PromoPlanningAgent agent = CreateAgent(mockClient.Object);
        Contracts.ChatResponse response = await agent.HandleAsync(
            new ChatRequest("promo?", SessionId: "s-err"));

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
        IChatClient chatClient = MockChatClient($"Promo plan for {brand}: 15% lift expected during campaign.");
        PromoPlanningAgent agent = CreateAgent(chatClient);

        Contracts.ChatResponse response = await agent.HandleAsync(
            new ChatRequest($"What's the promo plan for {brand}?", SessionId: $"brand-{brand}"));

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

    private static PromoPlanningAgent CreateAgent(
        IChatClient? chatClient = null,
        IEnumerable<AITool>? tools = null,
        IApprovalGate? approvalGate = null)
    {
        IHubContext<TelemetryHub> hubContext = CreateMockHubContext();
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        var pipeline = new AgentExecutionPipeline(
            chatClient ?? Mock.Of<IChatClient>(),
            hubContext,
            config,
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());

        return new PromoPlanningAgent(
            pipeline,
            new AgentDefinition
            {
                Name = "PromoPlanningAgent",
                Model = "gpt-5.4-mini",
                SystemPrompt = "You are a promotion planning specialist for retail brands.",
                Temperature = 0.3
            },
            tools ?? [],
            approvalGate);
    }

    private static Mock<IApprovalGate> CreateMockApprovalGate()
    {
        var mockGate = new Mock<IApprovalGate>();
        mockGate.Setup(g => g.RequestApprovalAsync(It.IsAny<ApprovalContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApprovalContext ctx, CancellationToken _) => new ApprovalRequest(
                "req-1", ctx, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)));
        mockGate.Setup(g => g.GetResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalResult("req-1", ApprovalDecision.Approved, "Auto-approved by test", DateTimeOffset.UtcNow));
        return mockGate;
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

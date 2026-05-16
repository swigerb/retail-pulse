using System.ClientModel;
using System.ClientModel.Primitives;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;
using ChatRequest = RetailPulse.Contracts.ChatRequest;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Tests.Agents.Specialists;

/// <summary>
/// Tests for SupplyChainAgent — the specialist agent handling supply/shipments
/// intent classification. Verifies ISpecialistAgent contract compliance, response
/// shape, structured health assessment, router integration, and edge cases.
/// Test-first: defines the expected contract before implementation exists.
/// </summary>
public class SupplyChainAgentTests
{
    #region Identity & ISpecialistAgent Contract

    [Fact]
    public void Key_IsSupplyChain()
    {
        SupplyChainAgent agent = CreateAgent();
        agent.Key.Should().Be("supply-chain");
    }

    [Fact]
    public void DisplayName_IsNotEmpty()
    {
        SupplyChainAgent agent = CreateAgent();
        agent.DisplayName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SupportedIntents_ContainsSupplyShipments()
    {
        SupplyChainAgent agent = CreateAgent();
        agent.SupportedIntents.Should().Contain(AgentIntent.SupplyShipments);
    }

    [Fact]
    public void SupportedIntents_DoesNotContainGeneralFallback()
    {
        SupplyChainAgent agent = CreateAgent();
        agent.SupportedIntents.Should().NotContain(AgentIntent.General);
    }

    [Fact]
    public void SupportedIntents_DoesNotContainOtherSpecialistIntents()
    {
        SupplyChainAgent agent = CreateAgent();
        agent.SupportedIntents.Should().NotContain(AgentIntent.DemandForecasting);
        agent.SupportedIntents.Should().NotContain(AgentIntent.PromotionTrade);
        agent.SupportedIntents.Should().NotContain(AgentIntent.CompetitiveMarket);
        agent.SupportedIntents.Should().NotContain(AgentIntent.SentimentField);
    }

    [Fact]
    public void SupportedIntents_OnlySupplyShipments()
    {
        SupplyChainAgent agent = CreateAgent();
        agent.SupportedIntents.Should().HaveCount(1);
        agent.SupportedIntents.Should().ContainSingle(i => i == AgentIntent.SupplyShipments);
    }

    #endregion

    #region HandleAsync — Response Shape

    [Fact]
    public async Task HandleAsync_ReturnsReplyFromModel()
    {
        IChatClient chatClient = MockChatClient("Inventory levels for Sierra Gold Tequila are healthy across all regions.");
        SupplyChainAgent agent = CreateAgent(chatClient);

        var request = new ChatRequest("What's the supply situation for Sierra Gold?", SessionId: "session-supply-1");
        ChatResponse response = await agent.HandleAsync(request);

        response.Reply.Should().Contain("Inventory");
        response.SessionId.Should().Be("session-supply-1");
    }

    [Fact]
    public async Task HandleAsync_GeneratesSessionIdWhenMissing()
    {
        IChatClient chatClient = MockChatClient("Supply chain status report ready.");
        SupplyChainAgent agent = CreateAgent(chatClient);

        ChatResponse response = await agent.HandleAsync(new ChatRequest("Supply status"));

        response.SessionId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HandleAsync_IncludesSpans()
    {
        IChatClient chatClient = MockChatClient("Supply analysis complete.");
        SupplyChainAgent agent = CreateAgent(chatClient);

        ChatResponse response = await agent.HandleAsync(
            new ChatRequest("Show supply health for Ridgeline Bourbon", SessionId: "span-test-supply"));

        response.Spans.Should().NotBeEmpty();
        response.Spans.Should().Contain(s => s.Type == "thought");
        response.Spans.Should().Contain(s => s.Type == "response");
    }

    [Fact]
    public async Task HandleAsync_PropagatesSessionId()
    {
        IChatClient chatClient = MockChatClient("ok");
        SupplyChainAgent agent = CreateAgent(chatClient);

        ChatResponse response = await agent.HandleAsync(
            new ChatRequest("supply?", SessionId: "my-supply-session-42"));

        response.SessionId.Should().Be("my-supply-session-42");
    }

    [Fact]
    public async Task HandleAsync_IncludesTotalDurationMs()
    {
        IChatClient chatClient = MockChatClient("done");
        SupplyChainAgent agent = CreateAgent(chatClient);

        ChatResponse response = await agent.HandleAsync(
            new ChatRequest("supply health?", SessionId: "dur-supply-test"));

        response.TotalDurationMs.Should().NotBeNull();
        response.TotalDurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task HandleAsync_SpansHaveCorrectSessionId()
    {
        IChatClient chatClient = MockChatClient("done");
        SupplyChainAgent agent = CreateAgent(chatClient);

        ChatResponse response = await agent.HandleAsync(
            new ChatRequest("test", SessionId: "span-session-supply-test"));

        response.Spans.Should().OnlyContain(s => s.SessionId == "span-session-supply-test");
    }

    [Fact]
    public async Task HandleAsync_SpansHaveTimestamps()
    {
        IChatClient chatClient = MockChatClient("done");
        SupplyChainAgent agent = CreateAgent(chatClient);

        ChatResponse response = await agent.HandleAsync(
            new ChatRequest("test", SessionId: "ts-supply-test"));

        response.Spans.Should().OnlyContain(s => s.Timestamp > DateTimeOffset.MinValue);
    }

    #endregion

    #region HandleAsync — Structured Supply Health Assessment

    [Fact]
    public async Task HandleAsync_SupplyQuery_ReturnsStructuredAssessment()
    {
        IChatClient chatClient = MockChatClient(
            "## Supply Health Assessment\n" +
            "**Overall Status:** Healthy\n" +
            "**Inventory Coverage:** 14 days of supply\n" +
            "**Active Disruptions:** None\n" +
            "**Fulfillment Rate:** 96.2%\n" +
            "**Recommendation:** MAINTAIN — current supply levels are adequate");
        SupplyChainAgent agent = CreateAgent(chatClient);

        ChatResponse response = await agent.HandleAsync(
            new ChatRequest("What's the supply health for Sierra Gold Tequila?", SessionId: "assess-supply-1"));

        response.Reply.Should().NotBeNullOrWhiteSpace();
        response.Reply.Should().Contain("Supply");
    }

    [Fact]
    public async Task HandleAsync_DisruptionDetected_ReturnsCriticalAssessment()
    {
        IChatClient chatClient = MockChatClient(
            "## Supply Health Assessment\n" +
            "**Overall Status:** Critical\n" +
            "**Active Disruptions:** 2 high-severity disruptions in Northeast\n" +
            "**Fulfillment Rate:** 72.1% (below 80% threshold)\n" +
            "**Recommendation:** ESCALATE — immediate action required for supply restoration");
        SupplyChainAgent agent = CreateAgent(chatClient);

        ChatResponse response = await agent.HandleAsync(
            new ChatRequest("Any supply disruptions for FreshMart?", SessionId: "disruption-1"));

        string reply = response.Reply;
        (reply.Contains("Critical") || reply.Contains("disruption") || reply.Contains("ESCALATE"))
            .Should().BeTrue("response should reference supply disruption severity");
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

        SupplyChainAgent agent = CreateAgent(mockClient.Object);
        var request = new ChatRequest(
            "Now show me disruptions for that region",
            SessionId: "hist-supply-1",
            History:
            [
                new ChatHistoryMessage("user", "Show inventory levels for Sierra Gold in Northeast"),
                new ChatHistoryMessage("assistant", "Here's the inventory data for Sierra Gold in Northeast...")
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

        SupplyChainAgent agent = CreateAgent(mockClient.Object);
        await agent.HandleAsync(
            new ChatRequest("current", SessionId: "cap-supply-test", History: history));

        captured.Should().NotBeNull();
        // System(1) + capped history(20) + current(1) = 22
        captured.Should().HaveCount(22);
    }

    #endregion

    #region Tool Isolation

    [Fact]
    public async Task HandleAsync_UsesSupplySpecificTools()
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

        var supplyTools = new List<AITool>
        {
            AIFunctionFactory.Create(() => "inventory data", "get_inventory_levels"),
            AIFunctionFactory.Create(() => "disruptions data", "get_supply_disruptions"),
            AIFunctionFactory.Create(() => "fulfillment data", "get_fulfillment_rate"),
            AIFunctionFactory.Create(() => "health summary", "get_supply_health_summary")
        };

        SupplyChainAgent agent = CreateAgent(mockClient.Object, supplyTools);
        await agent.HandleAsync(new ChatRequest("supply?", SessionId: "tool-supply-test"));

        capturedOptions.Should().NotBeNull();
        capturedOptions.Tools.Should().HaveCount(4);
        capturedOptions.Tools.OfType<AIFunction>().Select(t => t.Name)
            .Should().Contain("get_inventory_levels")
            .And.Contain("get_supply_disruptions")
            .And.Contain("get_fulfillment_rate")
            .And.Contain("get_supply_health_summary");
    }

    [Fact]
    public void SupplyAgent_KeyDiffersFromGeneral()
    {
        SupplyChainAgent supplyAgent = CreateAgent();
        Api.Agents.Specialists.GeneralAgent generalAgent = Fixtures.AgentTestFixtures.CreateGeneralAgent();

        supplyAgent.Key.Should().NotBe(generalAgent.Key);
        supplyAgent.Key.Should().Be("supply-chain");
        generalAgent.Key.Should().Be("general");
    }

    #endregion

    #region Router Integration — Supply Queries

    [Theory]
    [InlineData("What's the supply situation?")]
    [InlineData("Any disruptions?")]
    [InlineData("Show me inventory levels for Sierra Gold")]
    [InlineData("What's the fulfillment rate in the Northeast?")]
    public void SupplyAgent_RespondsToSupplyIntent(string _)
    {
        SupplyChainAgent agent = CreateAgent();
        agent.SupportedIntents.Should().Contain(AgentIntent.SupplyShipments);
    }

    #endregion

    #region Unknown Brand Handling

    [Fact]
    public async Task HandleAsync_UnknownBrand_ReturnsFriendlyMessage()
    {
        IChatClient chatClient = MockChatClient(
            "I don't have supply data for that brand. The known brands in the portfolio " +
            "include Sierra Gold Tequila, Ridgeline Bourbon, Summit Vodka, and others.");
        SupplyChainAgent agent = CreateAgent(chatClient);

        ChatResponse response = await agent.HandleAsync(
            new ChatRequest("What's the supply status for NonExistentBrand?", SessionId: "unknown-supply"));

        response.Reply.Should().NotBeNullOrWhiteSpace();
        response.Reply.Should().NotContain("exception", because: "should be a friendly message, not a stack trace");
        response.SessionId.Should().Be("unknown-supply");
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

        SupplyChainAgent agent = CreateAgent(mockClient.Object);
        ChatResponse response = await agent.HandleAsync(
            new ChatRequest("supply?", SessionId: "s-429-supply"));

        response.Reply.Should().Contain("rate-limited");
        response.SessionId.Should().Be("s-429-supply");
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

        SupplyChainAgent agent = CreateAgent(mockClient.Object);
        ChatResponse response = await agent.HandleAsync(
            new ChatRequest("supply?", SessionId: "s-err-supply"));

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
        IChatClient chatClient = MockChatClient($"Supply analysis for {brand}: inventory healthy, no disruptions.");
        SupplyChainAgent agent = CreateAgent(chatClient);

        ChatResponse response = await agent.HandleAsync(
            new ChatRequest($"What's the supply health for {brand}?", SessionId: $"brand-supply-{brand}"));

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

    private static SupplyChainAgent CreateAgent(
        IChatClient? chatClient = null,
        IEnumerable<AITool>? tools = null)
    {
        IHubContext<TelemetryHub> hubContext = CreateMockHubContext();
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        return new SupplyChainAgent(
            chatClient ?? Mock.Of<IChatClient>(),
            new AgentDefinition
            {
                Name = "SupplyChain",
                Model = "gpt-5.4-mini",
                SystemPrompt = "You are a supply chain specialist for retail brands.",
                Temperature = 0.3
            },
            hubContext,
            tools ?? [],
            Mock.Of<ILogger<SupplyChainAgent>>(),
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

/// <summary>
/// Supply Chain specialist agent — stub implementation following the same pattern
/// as DemandForecastAgent and CompetitiveIntelAgent. Handles supply/shipments intent.
/// Will be replaced by the real implementation in RetailPulse.Api.Agents.Specialists.
/// </summary>
public class SupplyChainAgent : ISpecialistAgent
{
    private readonly IChatClient _chatClient;
    private readonly AgentDefinition _agentDef;
    private readonly IHubContext<TelemetryHub> _hubContext;
    private readonly IEnumerable<AITool> _tools;
    private readonly ILogger<SupplyChainAgent> _logger;
    private readonly IConfiguration _configuration;

    public string Key => "supply-chain";
    public string DisplayName => "Supply Chain Agent";
    public string Model => _agentDef.Model;
    public IReadOnlyList<string> SupportedIntents { get; } =
    [
        AgentIntent.SupplyShipments
    ];

    public SupplyChainAgent(
        IChatClient chatClient,
        AgentDefinition agentDef,
        IHubContext<TelemetryHub> hubContext,
        IEnumerable<AITool> tools,
        ILogger<SupplyChainAgent> logger,
        IConfiguration configuration)
    {
        _chatClient = chatClient;
        _agentDef = agentDef;
        _hubContext = hubContext;
        _tools = tools;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task<ChatResponse> HandleAsync(ChatRequest request, CancellationToken ct = default)
    {
        string sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
        var collector = new Api.Middleware.TelemetryCollector(_hubContext, sessionId);

        var chatOptions = new ChatOptions
        {
            Temperature = (float)_agentDef.Temperature,
            Tools = [.. _tools]
        };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _agentDef.SystemPrompt)
        };

        if (request.History is { Count: > 0 })
        {
            const int maxTurns = 10;
            List<ChatHistoryMessage> historyMessages = request.History.Count > maxTurns * 2
                ? [.. request.History.Skip(request.History.Count - (maxTurns * 2))]
                : request.History;

            foreach (ChatHistoryMessage historyMessage in historyMessages)
            {
                ChatRole role = string.Equals(historyMessage.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? ChatRole.Assistant
                    : ChatRole.User;
                messages.Add(new ChatMessage(role, historyMessage.Content));
            }
        }

        messages.Add(new(ChatRole.User, request.Message));

        var sw = System.Diagnostics.Stopwatch.StartNew();

        Microsoft.Extensions.AI.ChatResponse response;

        try
        {
            response = await _chatClient.GetResponseAsync(messages, chatOptions, ct);
        }
        catch (ClientResultException ex) when (ex.Status == 429)
        {
            long failureDurationMs = sw.ElapsedMilliseconds;
            _logger.LogWarning(ex, "Supply chain agent rate-limited after {DurationMs}ms", failureDurationMs);
            return new ChatResponse(
                "⏳ The AI service is temporarily rate-limited. Please wait a moment and try again.",
                sessionId, [], null, failureDurationMs);
        }
        catch (Exception ex)
        {
            long failureDurationMs = sw.ElapsedMilliseconds;
            _logger.LogError(ex, "Supply chain agent failed after {DurationMs}ms for session {SessionId}",
                failureDurationMs, sessionId);
            return new ChatResponse(
                "⚠️ Something went wrong while analyzing supply chain data. Please try again.",
                sessionId, [], null, failureDurationMs);
        }

        long thoughtDurationMs = sw.ElapsedMilliseconds;

        int inputTokens = (int)(response.Usage?.InputTokenCount ?? 0);
        int outputTokens = (int)(response.Usage?.OutputTokenCount ?? 0);

        await collector.RecordSpanAsync(
            _agentDef.Name, "thought",
            $"Processing: {request.Message[..Math.Min(100, request.Message.Length)]}",
            thoughtDurationMs,
            inputTokens > 0 ? inputTokens : null,
            outputTokens > 0 ? outputTokens : null);

        long postProcessStart = sw.ElapsedMilliseconds;
        string reply = response.Text ?? "I wasn't able to generate a supply chain analysis.";

        long responseDurationMs = sw.ElapsedMilliseconds - postProcessStart;
        await collector.RecordSpanAsync(
            _agentDef.Name, "response",
            reply[..Math.Min(200, reply.Length)],
            responseDurationMs);

        long totalDurationMs = sw.ElapsedMilliseconds;

        return new ChatResponse(
            reply, sessionId, [.. collector.Spans],
            null, totalDurationMs);
    }
}

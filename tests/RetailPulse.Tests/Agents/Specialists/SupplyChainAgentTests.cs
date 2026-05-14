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
using System.ClientModel;
using System.ClientModel.Primitives;
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
        var agent = CreateAgent();
        agent.Key.Should().Be("supply-chain");
    }

    [Fact]
    public void DisplayName_IsNotEmpty()
    {
        var agent = CreateAgent();
        agent.DisplayName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SupportedIntents_ContainsSupplyShipments()
    {
        var agent = CreateAgent();
        agent.SupportedIntents.Should().Contain(AgentIntent.SupplyShipments);
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
        agent.SupportedIntents.Should().NotContain(AgentIntent.CompetitiveMarket);
        agent.SupportedIntents.Should().NotContain(AgentIntent.SentimentField);
    }

    [Fact]
    public void SupportedIntents_OnlySupplyShipments()
    {
        var agent = CreateAgent();
        agent.SupportedIntents.Should().HaveCount(1);
        agent.SupportedIntents.Should().ContainSingle(i => i == AgentIntent.SupplyShipments);
    }

    #endregion

    #region HandleAsync — Response Shape

    [Fact]
    public async Task HandleAsync_ReturnsReplyFromModel()
    {
        var chatClient = MockChatClient("Inventory levels for Sierra Gold Tequila are healthy across all regions.");
        var agent = CreateAgent(chatClient);

        var request = new ChatRequest("What's the supply situation for Sierra Gold?", SessionId: "session-supply-1");
        var response = await agent.HandleAsync(request);

        response.Reply.Should().Contain("Inventory");
        response.SessionId.Should().Be("session-supply-1");
    }

    [Fact]
    public async Task HandleAsync_GeneratesSessionIdWhenMissing()
    {
        var chatClient = MockChatClient("Supply chain status report ready.");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(new ChatRequest("Supply status"));

        response.SessionId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HandleAsync_IncludesSpans()
    {
        var chatClient = MockChatClient("Supply analysis complete.");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("Show supply health for Ridgeline Bourbon", SessionId: "span-test-supply"));

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
            new ChatRequest("supply?", SessionId: "my-supply-session-42"));

        response.SessionId.Should().Be("my-supply-session-42");
    }

    [Fact]
    public async Task HandleAsync_IncludesTotalDurationMs()
    {
        var chatClient = MockChatClient("done");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("supply health?", SessionId: "dur-supply-test"));

        response.TotalDurationMs.Should().NotBeNull();
        response.TotalDurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task HandleAsync_SpansHaveCorrectSessionId()
    {
        var chatClient = MockChatClient("done");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("test", SessionId: "span-session-supply-test"));

        response.Spans.Should().OnlyContain(s => s.SessionId == "span-session-supply-test");
    }

    [Fact]
    public async Task HandleAsync_SpansHaveTimestamps()
    {
        var chatClient = MockChatClient("done");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("test", SessionId: "ts-supply-test"));

        response.Spans.Should().OnlyContain(s => s.Timestamp > DateTimeOffset.MinValue);
    }

    #endregion

    #region HandleAsync — Structured Supply Health Assessment

    [Fact]
    public async Task HandleAsync_SupplyQuery_ReturnsStructuredAssessment()
    {
        var chatClient = MockChatClient(
            "## Supply Health Assessment\n" +
            "**Overall Status:** Healthy\n" +
            "**Inventory Coverage:** 14 days of supply\n" +
            "**Active Disruptions:** None\n" +
            "**Fulfillment Rate:** 96.2%\n" +
            "**Recommendation:** MAINTAIN — current supply levels are adequate");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("What's the supply health for Sierra Gold Tequila?", SessionId: "assess-supply-1"));

        response.Reply.Should().NotBeNullOrWhiteSpace();
        response.Reply.Should().Contain("Supply");
    }

    [Fact]
    public async Task HandleAsync_DisruptionDetected_ReturnsCriticalAssessment()
    {
        var chatClient = MockChatClient(
            "## Supply Health Assessment\n" +
            "**Overall Status:** Critical\n" +
            "**Active Disruptions:** 2 high-severity disruptions in Northeast\n" +
            "**Fulfillment Rate:** 72.1% (below 80% threshold)\n" +
            "**Recommendation:** ESCALATE — immediate action required for supply restoration");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("Any supply disruptions for FreshMart?", SessionId: "disruption-1"));

        var reply = response.Reply;
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
                captured = msgs.ToList())
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant, "done")));

        var agent = CreateAgent(mockClient.Object);
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
            new ChatRequest("current", SessionId: "cap-supply-test", History: history));

        captured.Should().NotBeNull();
        // System(1) + capped history(20) + current(1) = 22
        captured!.Should().HaveCount(22);
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

        var agent = CreateAgent(mockClient.Object, supplyTools);
        await agent.HandleAsync(new ChatRequest("supply?", SessionId: "tool-supply-test"));

        capturedOptions.Should().NotBeNull();
        capturedOptions!.Tools.Should().HaveCount(4);
        capturedOptions.Tools!.OfType<AIFunction>().Select(t => t.Name)
            .Should().Contain("get_inventory_levels")
            .And.Contain("get_supply_disruptions")
            .And.Contain("get_fulfillment_rate")
            .And.Contain("get_supply_health_summary");
    }

    [Fact]
    public void SupplyAgent_KeyDiffersFromGeneral()
    {
        var supplyAgent = CreateAgent();
        var generalAgent = Fixtures.AgentTestFixtures.CreateGeneralAgent();

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
        var agent = CreateAgent();
        agent.SupportedIntents.Should().Contain(AgentIntent.SupplyShipments);
    }

    #endregion

    #region Unknown Brand Handling

    [Fact]
    public async Task HandleAsync_UnknownBrand_ReturnsFriendlyMessage()
    {
        var chatClient = MockChatClient(
            "I don't have supply data for that brand. The known brands in the portfolio " +
            "include Sierra Gold Tequila, Ridgeline Bourbon, Summit Vodka, and others.");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
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

        var agent = CreateAgent(mockClient.Object);
        var response = await agent.HandleAsync(
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

        var agent = CreateAgent(mockClient.Object);
        var response = await agent.HandleAsync(
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
        var chatClient = MockChatClient($"Supply analysis for {brand}: inventory healthy, no disruptions.");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
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
        var hubContext = CreateMockHubContext();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
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
        var sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
        var collector = new Api.Middleware.TelemetryCollector(_hubContext, sessionId);

        var chatOptions = new ChatOptions
        {
            Temperature = (float)_agentDef.Temperature,
            Tools = _tools.ToList()
        };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _agentDef.SystemPrompt)
        };

        if (request.History is { Count: > 0 })
        {
            const int maxTurns = 10;
            var historyMessages = request.History.Count > maxTurns * 2
                ? request.History.Skip(request.History.Count - (maxTurns * 2)).ToList()
                : request.History;

            foreach (var historyMessage in historyMessages)
            {
                var role = string.Equals(historyMessage.Role, "assistant", StringComparison.OrdinalIgnoreCase)
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
            var failureDurationMs = sw.ElapsedMilliseconds;
            _logger.LogWarning(ex, "Supply chain agent rate-limited after {DurationMs}ms", failureDurationMs);
            return new ChatResponse(
                "⏳ The AI service is temporarily rate-limited. Please wait a moment and try again.",
                sessionId, [], null, failureDurationMs);
        }
        catch (Exception ex)
        {
            var failureDurationMs = sw.ElapsedMilliseconds;
            _logger.LogError(ex, "Supply chain agent failed after {DurationMs}ms for session {SessionId}",
                failureDurationMs, sessionId);
            return new ChatResponse(
                "⚠️ Something went wrong while analyzing supply chain data. Please try again.",
                sessionId, [], null, failureDurationMs);
        }

        var thoughtDurationMs = sw.ElapsedMilliseconds;

        var inputTokens = (int)(response.Usage?.InputTokenCount ?? 0);
        var outputTokens = (int)(response.Usage?.OutputTokenCount ?? 0);

        await collector.RecordSpanAsync(
            _agentDef.Name, "thought",
            $"Processing: {request.Message[..Math.Min(100, request.Message.Length)]}",
            thoughtDurationMs,
            inputTokens > 0 ? (int?)inputTokens : null,
            outputTokens > 0 ? (int?)outputTokens : null);

        var postProcessStart = sw.ElapsedMilliseconds;
        var reply = response.Text ?? "I wasn't able to generate a supply chain analysis.";

        var responseDurationMs = sw.ElapsedMilliseconds - postProcessStart;
        await collector.RecordSpanAsync(
            _agentDef.Name, "response",
            reply[..Math.Min(200, reply.Length)],
            responseDurationMs);

        var totalDurationMs = sw.ElapsedMilliseconds;

        return new ChatResponse(
            reply, sessionId, collector.Spans.ToList(),
            null, totalDurationMs);
    }
}

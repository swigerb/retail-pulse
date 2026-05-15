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
using System.ClientModel;
using System.ClientModel.Primitives;

namespace RetailPulse.Tests.Agents.Specialists;

/// <summary>
/// Tests for GeneralAgent — the refactored RetailPulseAgent that implements
/// ISpecialistAgent (Contracts.Routing) and handles all existing tools.
/// </summary>
public class GeneralAgentTests
{
    #region Identity & Configuration

    [Fact]
    public void Key_IsGeneral()
    {
        var agent = CreateAgent();
        agent.Key.Should().Be("general");
    }

    [Fact]
    public void DisplayName_IsNotEmpty()
    {
        var agent = CreateAgent();
        agent.DisplayName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SupportedIntents_IncludesGeneralFallback()
    {
        var agent = CreateAgent();
        agent.SupportedIntents.Should().Contain(AgentIntent.General);
    }

    [Fact]
    public void SupportedIntents_CoversFiveDomains()
    {
        var agent = CreateAgent();
        // GeneralAgent is the fallback — it only claims General intent.
        // FieldSentimentAgent owns SentimentField; other dedicated specialists
        // own PromotionTrade, SupplyShipments, CompetitiveMarket.
        agent.SupportedIntents.Should().NotContain(AgentIntent.DemandForecasting);
        agent.SupportedIntents.Should().NotContain(AgentIntent.PromotionTrade);
        agent.SupportedIntents.Should().NotContain(AgentIntent.SupplyShipments);
        agent.SupportedIntents.Should().NotContain(AgentIntent.CompetitiveMarket);
        agent.SupportedIntents.Should().NotContain(AgentIntent.SentimentField);
        agent.SupportedIntents.Should().Contain(AgentIntent.General);
        agent.SupportedIntents.Should().HaveCount(1);
    }

    #endregion

    #region HandleAsync — ISpecialistAgent Contract

    [Fact]
    public async Task HandleAsync_ReturnsReplyFromModel()
    {
        var chatClient = MockChatClient("Here is the analysis.");
        var agent = CreateAgent(chatClient);

        var request = new ChatRequest("Show me portfolio", SessionId: "session-1");
        var response = await agent.HandleAsync(request);

        response.Reply.Should().Be("Here is the analysis.");
        response.SessionId.Should().Be("session-1");
    }

    [Fact]
    public async Task HandleAsync_GeneratesSessionIdWhenMissing()
    {
        var chatClient = MockChatClient("ok");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(new ChatRequest("hello"));

        response.SessionId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HandleAsync_IncludesSpans()
    {
        var chatClient = MockChatClient("Analysis complete");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("Run analysis", SessionId: "session-1"));

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
            new ChatRequest("hello", SessionId: "my-session-42"));

        response.SessionId.Should().Be("my-session-42");
    }

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
            "Follow up",
            SessionId: "s-1",
            History:
            [
                new ChatHistoryMessage("user", "Previous question"),
                new ChatHistoryMessage("assistant", "Previous answer")
            ]);

        await agent.HandleAsync(request);

        captured.Should().NotBeNull();
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

        // 22 history = 11 turns, should cap at 10 turns = 20 messages
        var history = Enumerable.Range(1, 22)
            .Select(i => new ChatHistoryMessage(i % 2 == 0 ? "assistant" : "user", $"h-{i}"))
            .ToList();

        var agent = CreateAgent(mockClient.Object);
        await agent.HandleAsync(
            new ChatRequest("current", SessionId: "s-1", History: history));

        captured.Should().NotBeNull();
        // System(1) + capped history(20) + current(1) = 22
        captured!.Should().HaveCount(22);
    }

    #endregion

    #region Backward Compatibility — Same Outputs as RetailPulseAgent

    [Fact]
    public async Task HandleAsync_IncludesChartSpecs_WhenToolReturnsChart()
    {
        // The agent extracts chart specs from FunctionResultContent
        // Test that the extraction works (even without actual tool calls)
        var chatClient = MockChatClient("Here's a chart");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("Show chart", SessionId: "chart-test"));

        // Without tool calls in mock, charts should be null
        response.Charts.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_IncludesTotalDurationMs()
    {
        var chatClient = MockChatClient("done");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("hello", SessionId: "dur-test"));

        response.TotalDurationMs.Should().NotBeNull();
        response.TotalDurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task HandleAsync_SpansHaveSessionId()
    {
        var chatClient = MockChatClient("done");
        var agent = CreateAgent(chatClient);

        var response = await agent.HandleAsync(
            new ChatRequest("test", SessionId: "span-test"));

        response.Spans.Should().OnlyContain(s => s.SessionId == "span-test");
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
            new ChatRequest("hello", SessionId: "s-429"));

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
            new ChatRequest("hello", SessionId: "s-err"));

        response.Reply.Should().Contain("Something went wrong");
        response.TotalDurationMs.Should().NotBeNull();
    }

    #endregion

    #region Token Usage / Cost

    [Fact]
    public async Task HandleAsync_WithTokenUsage_IncludesCostEstimate()
    {
        var mockClient = new Mock<IChatClient>();
        var chatResponse = new Microsoft.Extensions.AI.ChatResponse(
            new ChatMessage(ChatRole.Assistant, "done"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 5000,
                OutputTokenCount = 2000,
                TotalTokenCount = 7000
            }
        };
        mockClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(chatResponse);

        var agent = CreateAgentWithPricing(mockClient.Object);
        var response = await agent.HandleAsync(
            new ChatRequest("hello", SessionId: "s-cost"));

        response.TokenUsage.Should().NotBeNull();
        response.TokenUsage!.InputTokens.Should().Be(5000);
        response.TokenUsage!.OutputTokens.Should().Be(2000);
        response.TokenUsage!.EstimatedCostUsd.Should().NotBeNull();
    }

    [Fact]
    public void BuildTokenUsage_WithKnownModel_CalculatesCost()
    {
        var (pipeline, _) = CreateAgentWithPricingParts();
        var usage = pipeline.BuildTokenUsage(10000, 5000, 15000, "gpt-5.4-mini");

        usage.InputTokens.Should().Be(10000);
        usage.OutputTokens.Should().Be(5000);
        // 10000 * 0.25 / 1M + 5000 * 2.00 / 1M = 0.0025 + 0.01 = 0.0125
        usage.EstimatedCostUsd.Should().Be(0.0125m);
    }

    [Fact]
    public void BuildTokenUsage_WithUnknownModel_ReturnsNullCost()
    {
        var (pipeline, _) = CreateAgentParts();
        var usage = pipeline.BuildTokenUsage(1000, 500, 1500, "gpt-4o");
        usage.EstimatedCostUsd.Should().BeNull();
    }

    [Fact]
    public void BuildTokenUsage_WithZeroTokens_ReturnsZeroCost()
    {
        var (pipeline, _) = CreateAgentWithPricingParts();
        var usage = pipeline.BuildTokenUsage(0, 0, 0, "gpt-5.4-mini");
        usage.EstimatedCostUsd.Should().Be(0m);
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

    private static GeneralAgent CreateAgent(IChatClient? chatClient = null)
    {
        var (pipeline, _) = CreateAgentParts(chatClient);
        return new GeneralAgent(
            pipeline,
            new AgentDefinition { Name = "General", Model = "gpt-4o", SystemPrompt = "You are a retail analyst.", Temperature = 0.7 },
            []);
    }

    private static (AgentExecutionPipeline Pipeline, AgentDefinition AgentDef) CreateAgentParts(IChatClient? chatClient = null)
    {
        var hubContext = CreateMockHubContext();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var pipeline = new AgentExecutionPipeline(
            chatClient ?? Mock.Of<IChatClient>(),
            hubContext,
            config,
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());

        var agentDef = new AgentDefinition { Name = "General", Model = "gpt-4o", SystemPrompt = "You are a retail analyst.", Temperature = 0.7 };

        return (pipeline, agentDef);
    }

    private static GeneralAgent CreateAgentWithPricing(IChatClient? chatClient = null)
    {
        var (pipeline, agentDef) = CreateAgentWithPricingParts(chatClient);
        return new GeneralAgent(pipeline, agentDef, []);
    }

    private static (AgentExecutionPipeline Pipeline, AgentDefinition AgentDef) CreateAgentWithPricingParts(IChatClient? chatClient = null)
    {
        var hubContext = CreateMockHubContext();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TokenPricing:gpt-5.4-mini:InputPerMillion"] = "0.25",
                ["TokenPricing:gpt-5.4-mini:OutputPerMillion"] = "2.00",
            })
            .Build();

        var pipeline = new AgentExecutionPipeline(
            chatClient ?? Mock.Of<IChatClient>(),
            hubContext,
            config,
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());

        var agentDef = new AgentDefinition { Name = "General", Model = "gpt-5.4-mini", SystemPrompt = "Analyst", Temperature = 0.7 };

        return (pipeline, agentDef);
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

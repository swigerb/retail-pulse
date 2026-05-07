using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace RetailPulse.Tests;

public class RetailPulseAgentTests
{
    [Fact]
    public async Task ChatAsync_IncludesConversationHistoryBeforeCurrentMessage()
    {
        List<ChatMessage>? capturedMessages = null;
        var chatClient = new Mock<IChatClient>();
        chatClient
            .Setup(x => x.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((messages, _, _) => capturedMessages = messages.ToList())
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));

        var agent = CreateAgent(chatClient.Object);
        var request = new ChatRequest(
            "rank them by sell-through growth",
            SessionId: "session-1",
            User: null,
            History:
            [
                new ChatHistoryMessage("user", "Which brands are growing fastest year-over-year?"),
                new ChatHistoryMessage("assistant", "FreshMart and Harvest Table are leading growth.")
            ]);

        var response = await agent.ChatAsync(request);

        response.Reply.Should().Be("done");
        capturedMessages.Should().NotBeNull();
        capturedMessages!.Select(m => m.Role).Should().ContainInOrder(
            ChatRole.System,
            ChatRole.User,
            ChatRole.Assistant,
            ChatRole.User);
        capturedMessages.Select(m => m.Text).Should().ContainInOrder(
            "System prompt",
            "Which brands are growing fastest year-over-year?",
            "FreshMart and Harvest Table are leading growth.",
            "rank them by sell-through growth");
    }

    [Fact]
    public async Task ChatAsync_CapsConversationHistoryAtLastTenTurns()
    {
        List<ChatMessage>? capturedMessages = null;
        var chatClient = new Mock<IChatClient>();
        chatClient
            .Setup(x => x.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((messages, _, _) => capturedMessages = messages.ToList())
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));

        var history = Enumerable.Range(1, 22)
            .Select(i => new ChatHistoryMessage(i % 2 == 0 ? "assistant" : "user", $"history-{i}"))
            .ToList();

        var agent = CreateAgent(chatClient.Object);
        await agent.ChatAsync(new ChatRequest("current", SessionId: "session-1", History: history));

        capturedMessages.Should().NotBeNull();
        capturedMessages!.Should().HaveCount(22);
        capturedMessages[0].Role.Should().Be(ChatRole.System);
        capturedMessages[1].Text.Should().Be("history-3");
        capturedMessages[20].Text.Should().Be("history-22");
        capturedMessages[21].Text.Should().Be("current");
    }

    [Fact]
    public async Task ChatAsync_WhenRateLimited_ReturnsFriendlyResponse()
    {
        var chatClient = new Mock<IChatClient>();
        chatClient
            .Setup(x => x.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ClientResultException("Too Many Requests", CreatePipelineResponse(429), null));

        var agent = CreateAgent(chatClient.Object);

        var response = await agent.ChatAsync(new ChatRequest("hello", SessionId: "session-429"));

        response.Reply.Should().Be("⏳ The AI service is temporarily rate-limited. Please wait a moment and try again. (APIM token limit: 10,000 TPM)");
        response.SessionId.Should().Be("session-429");
        response.Spans.Should().BeEmpty();
        response.Charts.Should().BeNull();
        response.TotalDurationMs.Should().NotBeNull();
    }

    [Fact]
    public async Task ChatAsync_WhenUnexpectedExceptionOccurs_ReturnsFriendlyResponse()
    {
        var chatClient = new Mock<IChatClient>();
        chatClient
            .Setup(x => x.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var agent = CreateAgent(chatClient.Object);

        var response = await agent.ChatAsync(new ChatRequest("hello", SessionId: "session-error"));

        response.Reply.Should().Be("⚠️ Something went wrong while contacting the AI service. Please try again in a moment.");
        response.SessionId.Should().Be("session-error");
        response.Spans.Should().BeEmpty();
        response.Charts.Should().BeNull();
        response.TotalDurationMs.Should().NotBeNull();
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

    [Fact]
    public void BuildTokenUsage_WithMatchingModel_CalculatesCost()
    {
        var agent = CreateAgentWithModel("gpt-5.4-mini", new Dictionary<string, string?>
        {
            ["TokenPricing:gpt-5.4-mini:InputPerMillion"] = "0.25",
            ["TokenPricing:gpt-5.4-mini:OutputPerMillion"] = "2.00",
        });

        var usage = agent.BuildTokenUsage(5000, 2000, 7000);

        usage.InputTokens.Should().Be(5000);
        usage.OutputTokens.Should().Be(2000);
        usage.TotalTokens.Should().Be(7000);
        // Input: 5000 * 0.25 / 1M = 0.00125, Output: 2000 * 2.00 / 1M = 0.004
        usage.EstimatedCostUsd.Should().Be(0.00525m);
    }

    [Fact]
    public void BuildTokenUsage_WithUnknownModel_ReturnsNullCost()
    {
        var agent = CreateAgentWithModel("unknown-model", new Dictionary<string, string?>
        {
            ["TokenPricing:gpt-4o:InputPerMillion"] = "2.50",
            ["TokenPricing:gpt-4o:OutputPerMillion"] = "10.00",
        });

        var usage = agent.BuildTokenUsage(1000, 500, 1500);

        usage.EstimatedCostUsd.Should().BeNull();
    }

    [Fact]
    public void BuildTokenUsage_WithZeroTokens_ReturnsZeroCost()
    {
        var agent = CreateAgentWithModel("gpt-4o", new Dictionary<string, string?>
        {
            ["TokenPricing:gpt-4o:InputPerMillion"] = "2.50",
            ["TokenPricing:gpt-4o:OutputPerMillion"] = "10.00",
        });

        var usage = agent.BuildTokenUsage(0, 0, 0);

        usage.EstimatedCostUsd.Should().Be(0m);
    }

    [Fact]
    public async Task ChatAsync_WithTokenUsage_ReturnsCostInResponse()
    {
        var chatClient = new Mock<IChatClient>();
        var chatResponse = new Microsoft.Extensions.AI.ChatResponse(
            new ChatMessage(ChatRole.Assistant, "done"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 10000,
                OutputTokenCount = 5000,
                TotalTokenCount = 15000,
            }
        };
        chatClient
            .Setup(x => x.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(chatResponse);

        var agent = CreateAgentWithModel("gpt-5.4-mini", new Dictionary<string, string?>
        {
            ["TokenPricing:gpt-5.4-mini:InputPerMillion"] = "0.25",
            ["TokenPricing:gpt-5.4-mini:OutputPerMillion"] = "2.00",
        }, chatClient.Object);

        var response = await agent.ChatAsync(new ChatRequest("hello", SessionId: "cost-test"));

        response.TokenUsage.Should().NotBeNull();
        response.TokenUsage!.InputTokens.Should().Be(10000);
        response.TokenUsage!.OutputTokens.Should().Be(5000);
        // Input: 10000 * 0.25 / 1M = 0.0025, Output: 5000 * 2.00 / 1M = 0.01
        response.TokenUsage!.EstimatedCostUsd.Should().Be(0.0125m);
    }

    private static RetailPulseAgent CreateAgent(IChatClient chatClient)
    {
        return CreateAgentWithModel("gpt-4o", new Dictionary<string, string?>
        {
            ["TokenPricing:gpt-4o:InputPerMillion"] = "2.50",
            ["TokenPricing:gpt-4o:OutputPerMillion"] = "10.00",
        }, chatClient);
    }

    private static RetailPulseAgent CreateAgentWithModel(
        string model,
        Dictionary<string, string?> pricingConfig,
        IChatClient? chatClient = null)
    {
        var hubContext = new Mock<IHubContext<TelemetryHub>>();
        var clients = new Mock<IHubClients>();
        var allProxy = new Mock<IClientProxy>();
        clients.Setup(c => c.All).Returns(allProxy.Object);
        hubContext.Setup(h => h.Clients).Returns(clients.Object);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(pricingConfig)
            .Build();

        return new RetailPulseAgent(
            chatClient ?? Mock.Of<IChatClient>(),
            new AgentDefinition { Name = "Retail Pulse", Model = model, SystemPrompt = "System prompt" },
            hubContext.Object,
            [],
            Mock.Of<ILogger<RetailPulseAgent>>(),
            configuration);
    }
}

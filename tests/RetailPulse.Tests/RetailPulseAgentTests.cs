using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;

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

    private static RetailPulseAgent CreateAgent(IChatClient chatClient)
    {
        var hubContext = new Mock<IHubContext<TelemetryHub>>();
        var clients = new Mock<IHubClients>();
        var allProxy = new Mock<IClientProxy>();
        clients.Setup(c => c.All).Returns(allProxy.Object);
        hubContext.Setup(h => h.Clients).Returns(clients.Object);

        return new RetailPulseAgent(
            chatClient,
            new AgentDefinition { Name = "Retail Pulse", SystemPrompt = "System prompt" },
            hubContext.Object,
            [],
            Mock.Of<ILogger<RetailPulseAgent>>());
    }
}

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

namespace RetailPulse.Tests.Fixtures;

/// <summary>
/// Shared test fixtures and factory methods for router/agent tests.
/// Provides consistent mock configurations across test classes.
/// </summary>
public static class AgentTestFixtures
{
    /// <summary>
    /// Creates a mock IChatClient that returns a fixed response text.
    /// </summary>
    public static IChatClient CreateMockChatClient(string responseText)
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

    /// <summary>
    /// Creates a mock IChatClient that captures the messages sent to it.
    /// </summary>
    public static (IChatClient Client, Func<List<ChatMessage>?> GetCaptured) CreateCapturingChatClient(string responseText)
    {
        List<ChatMessage>? captured = null;
        var mock = new Mock<IChatClient>();
        mock
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((msgs, _, _) =>
                captured = msgs.ToList())
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant, responseText)));
        return (mock.Object, () => captured);
    }

    /// <summary>
    /// Creates a mock ISpecialistAgent (Contracts.Routing namespace).
    /// </summary>
    public static ISpecialistAgent CreateMockSpecialist(
        string key,
        IReadOnlyList<string> supportedIntents,
        string displayName = "Mock Agent",
        string model = "gpt-4o")
    {
        var mock = new Mock<ISpecialistAgent>();
        mock.Setup(a => a.Key).Returns(key);
        mock.Setup(a => a.DisplayName).Returns(displayName);
        mock.Setup(a => a.Model).Returns(model);
        mock.Setup(a => a.SupportedIntents).Returns(supportedIntents);
        mock.Setup(a => a.HandleAsync(
                It.IsAny<ChatRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RetailPulse.Contracts.ChatResponse("Mock response", "session-mock", []));
        return mock.Object;
    }

    /// <summary>
    /// Creates a mock IHubContext for TelemetryHub.
    /// </summary>
    public static IHubContext<TelemetryHub> CreateMockHubContext()
    {
        var hubContext = new Mock<IHubContext<TelemetryHub>>();
        var clients = new Mock<IHubClients>();
        var groupProxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(groupProxy.Object);
        hubContext.Setup(h => h.Clients).Returns(clients.Object);
        return hubContext.Object;
    }

    /// <summary>
    /// Creates a GeneralAgent with standard test configuration.
    /// </summary>
    public static GeneralAgent CreateGeneralAgent(
        IChatClient? chatClient = null,
        IEnumerable<AITool>? tools = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        return new GeneralAgent(
            chatClient ?? Mock.Of<IChatClient>(),
            new AgentDefinition
            {
                Name = "General",
                Model = "gpt-4o",
                SystemPrompt = "You are a retail analytics assistant.",
                Temperature = 0.7
            },
            CreateMockHubContext(),
            tools ?? [],
            Mock.Of<ILogger<GeneralAgent>>(),
            config);
    }

    /// <summary>
    /// Standard router AgentDefinition for tests.
    /// </summary>
    public static AgentDefinition RouterDefinition => new()
    {
        Name = "Router",
        Model = "gpt-5.4-mini",
        SystemPrompt = "Classify user intent. Return JSON with intent, confidence, intents array.",
        Temperature = 0.1
    };
}

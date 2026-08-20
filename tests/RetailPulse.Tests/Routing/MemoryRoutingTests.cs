using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Agents.Routing;
using RetailPulse.Api.Models;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Tests.Routing;

public class MemoryRoutingTests
{
    [Theory]
    [InlineData("Remember that ClearDesk is trending positive")]
    [InlineData("Remember this for next time: margins are up")]
    [InlineData("Remember ClearDesk is trending modestly positive in the Northeast this quarter")]
    [InlineData("Remember that ClearDesk depletions are trending in the Northeast this quarter")]
    public async Task Route_StorePhrases_RoutesToMemoryManagement(string message)
    {
        RoutingDecision decision = await BuildRouter().RouteAsync(message, null, null, null);
        decision.Intent.Should().Be(AgentIntent.MemoryManagement);
    }

    [Theory]
    [InlineData("What do you remember about ClearDesk?")]
    [InlineData("I'm focused on the Spirits category, especially premium tequila positioning")]
    public async Task Route_RecallAndPreferencePhrases_DoNotRouteToMemoryManagement(string message)
    {
        RoutingDecision decision = await BuildRouter().RouteAsync(message, null, null, null);
        decision.Intent.Should().NotBe(AgentIntent.MemoryManagement);
    }

    [Theory]
    [InlineData("Forget everything")]
    [InlineData("Clear my history")]
    [InlineData("Clear my data")]
    [InlineData("Start fresh")]
    [InlineData("Reset my context")]
    [InlineData("Forget what I told you")]
    [InlineData("What do you know about me?")]
    public async Task Route_ClearPhrases_RouteToMemoryManagement(string message)
    {
        RoutingDecision decision = await BuildRouter().RouteAsync(message, null, null, null);
        decision.Intent.Should().Be(AgentIntent.MemoryManagement);
    }

    private static RetailOpsRouter BuildRouter()
    {
        // LLM classifies to General with low confidence so keyword fast-paths take priority;
        // the recall/preference cases fall through to General on purpose.
        Mock<IChatClient> chatClient = new();
        chatClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                $"{{\"intent\":\"{AgentIntent.General}\",\"confidence\":0.5}}")));

        AgentDefinition routerDef = new()
        {
            Name = "Router",
            Model = "gpt-5.4-mini-router",
            SystemPrompt = "Classify intent.",
            Temperature = 0.0
        };

        Mock<ISpecialistAgent> memory = new();
        memory.SetupGet(s => s.Key).Returns("memory-management");
        memory.SetupGet(s => s.SupportedIntents).Returns([AgentIntent.MemoryManagement]);
        memory.SetupGet(s => s.KeywordFastPaths).Returns(
        [
            "remember that", "remember this", "forget", "clear my", "clear my history",
            "clear my data", "start fresh", "reset my context", "forget what I told you",
            "what do you know about me"
        ]);

        Mock<ISpecialistAgent> general = new();
        general.SetupGet(s => s.Key).Returns("general");
        general.SetupGet(s => s.SupportedIntents).Returns([AgentIntent.General]);
        general.SetupGet(s => s.KeywordFastPaths).Returns([]);

        return new RetailOpsRouter(
            chatClient.Object,
            routerDef,
            [memory.Object, general.Object],
            Mock.Of<ILogger<RetailOpsRouter>>());
    }
}

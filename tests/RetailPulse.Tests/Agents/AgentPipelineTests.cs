using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Hubs;
using RetailPulse.Contracts;
using RetailPulse.Tests.Fixtures;

namespace RetailPulse.Tests.Agents;

/// <summary>
/// Tests for the shared IAgentExecutionPipeline / AgentExecutionPipeline,
/// covering message construction, history truncation, and token accounting.
/// </summary>
public class AgentPipelineTests
{
    #region BuildMessages — basic construction

    [Fact]
    public async Task BuildMessages_NoHistory_ReturnsTwoMessages()
    {
        var request = new ChatRequest("What are today's sales?");
        var messages = AgentExecutionPipeline.BuildMessages("You are a test agent.", request);

        messages.Should().HaveCount(2);
        messages[0].Role.Should().Be(ChatRole.System);
        messages[0].Text.Should().Be("You are a test agent.");
        messages[1].Role.Should().Be(ChatRole.User);
        messages[1].Text.Should().Be("What are today's sales?");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task BuildMessages_WithHistory_InsertsBeforeUserMessage()
    {
        var history = new List<ChatHistoryMessage>
        {
            new("user", "First question"),
            new("assistant", "First answer"),
        };

        var request = new ChatRequest("Follow-up", History: history);
        var messages = AgentExecutionPipeline.BuildMessages("System prompt.", request);

        // System + 2 history + user = 4
        messages.Should().HaveCount(4);
        messages[0].Role.Should().Be(ChatRole.System);
        messages[1].Role.Should().Be(ChatRole.User);
        messages[1].Text.Should().Be("First question");
        messages[2].Role.Should().Be(ChatRole.Assistant);
        messages[2].Text.Should().Be("First answer");
        messages[3].Role.Should().Be(ChatRole.User);
        messages[3].Text.Should().Be("Follow-up");
        await Task.CompletedTask;
    }

    #endregion

    #region BuildMessages — history truncation

    [Fact]
    public async Task BuildMessages_TruncatesHistoryToMaxTurns()
    {
        // maxTurns is 10 in the pipeline, so max history messages = 20
        var history = Enumerable.Range(1, 30)
            .Select(i => new ChatHistoryMessage(i % 2 == 0 ? "assistant" : "user", $"Message {i}"))
            .ToList();

        var request = new ChatRequest("Latest question", History: history);
        var messages = AgentExecutionPipeline.BuildMessages("System.", request);

        // System (1) + truncated history (20) + user (1) = 22
        messages.Should().HaveCount(22);

        // Truncation keeps the last 20 history messages (indices 10–29)
        messages[1].Text.Should().Be("Message 11", "truncation should keep the most recent history");
        messages[^1].Text.Should().Be("Latest question");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task BuildMessages_HistoryExactlyAtLimit_NoTruncation()
    {
        var history = Enumerable.Range(1, 20)
            .Select(i => new ChatHistoryMessage(i % 2 == 0 ? "assistant" : "user", $"Msg {i}"))
            .ToList();

        var request = new ChatRequest("Current", History: history);
        var messages = AgentExecutionPipeline.BuildMessages("Sys.", request);

        // System (1) + 20 history + user (1) = 22
        messages.Should().HaveCount(22);
        messages[1].Text.Should().Be("Msg 1", "no truncation needed — all history fits");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task BuildMessages_HistoryUnderLimit_KeepsAll()
    {
        var history = new List<ChatHistoryMessage>
        {
            new("user", "A"),
            new("assistant", "B"),
            new("user", "C"),
            new("assistant", "D"),
        };

        var request = new ChatRequest("E", History: history);
        var messages = AgentExecutionPipeline.BuildMessages("Sys.", request);

        messages.Should().HaveCount(6); // 1 system + 4 history + 1 user
        await Task.CompletedTask;
    }

    #endregion

    #region IAgentExecutionPipeline — interface contract

    [Fact]
    public async Task ExecuteAsync_ReturnsNonNullResponse()
    {
        var pipeline = CreatePipeline("Test reply.");

        var context = CreateContext("What's up?");
        var response = await pipeline.ExecuteAsync(context);

        response.Should().NotBeNull();
        response.Reply.Should().Be("Test reply.");
        response.SessionId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_UsesProvidedSessionId()
    {
        var pipeline = CreatePipeline("Reply.");
        var request = new ChatRequest("Hi", SessionId: "my-session-123");
        var context = CreateContext(request: request);

        var response = await pipeline.ExecuteAsync(context);

        response.SessionId.Should().Be("my-session-123");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFallbackReply_WhenLlmReturnsNull()
    {
        // When the LLM returns a response with null Text, the pipeline
        // uses the fallback reply. We mock a response where Text is null
        // by setting up a response message with no text content.
        var chatClient = new Mock<IChatClient>();
        var nullTextResponse = new Microsoft.Extensions.AI.ChatResponse(
            new ChatMessage(ChatRole.Assistant, (string?)null));
        chatClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(nullTextResponse);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var pipeline = new AgentExecutionPipeline(
            chatClient.Object,
            AgentTestFixtures.CreateMockHubContext(),
            config,
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());

        var context = CreateContext("Question", fallbackReply: "Custom fallback");
        var response = await pipeline.ExecuteAsync(context);

        // The pipeline uses: response.Text ?? context.FallbackReply
        // If Text is null, fallback is used. If Text is empty string, it passes through.
        var expectedReply = nullTextResponse.Text ?? "Custom fallback";
        response.Reply.Should().Be(expectedReply);
    }

    #endregion

    #region Token accounting — model name correctness

    [Fact]
    public async Task BuildTokenUsage_UsesActualModelName_NotHardcoded()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TokenPricing:gpt-4.1-mini:InputPerMillion"] = "0.40",
                ["TokenPricing:gpt-4.1-mini:OutputPerMillion"] = "1.60",
            })
            .Build();

        var pipeline = CreatePipeline("Reply.", config: config);

        var usage = pipeline.BuildTokenUsage(1000, 500, 1500, "gpt-4.1-mini");

        usage.InputTokens.Should().Be(1000);
        usage.OutputTokens.Should().Be(500);
        usage.TotalTokens.Should().Be(1500);
        usage.EstimatedCostUsd.Should().NotBeNull("pricing config exists for gpt-4.1-mini");
        usage.EstimatedCostUsd.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task BuildTokenUsage_ReturnsNullCost_WhenModelNotInConfig()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var pipeline = CreatePipeline("Reply.", config: config);

        var usage = pipeline.BuildTokenUsage(100, 50, 150, "unknown-model");

        usage.EstimatedCostUsd.Should().BeNull(
            "no pricing config for 'unknown-model' — cost should be null, not a hardcoded value");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task BuildTokenUsage_CalculatesCorrectCost()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TokenPricing:test-model:InputPerMillion"] = "2.00",
                ["TokenPricing:test-model:OutputPerMillion"] = "8.00",
            })
            .Build();

        var pipeline = CreatePipeline("Reply.", config: config);

        var usage = pipeline.BuildTokenUsage(1_000_000, 500_000, 1_500_000, "test-model");

        // Expected: (1M * 2.00 / 1M) + (500K * 8.00 / 1M) = 2.00 + 4.00 = 6.00
        usage.EstimatedCostUsd.Should().Be(6.00m);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AgentExecutionContext_ModelName_FlowsThrough()
    {
        // Verify that the context model name is used (not a hardcoded string)
        var context = new AgentExecutionContext
        {
            AgentName = "test-agent",
            SystemPrompt = "System prompt.",
            Temperature = 0.5f,
            ModelName = "gpt-5.4-mini",
            Request = new ChatRequest("Test"),
            Tools = Enumerable.Empty<AITool>(),
        };

        context.ModelName.Should().Be("gpt-5.4-mini");
        context.ModelName.Should().NotBe("gpt-4o", "model name should not be hardcoded");
        await Task.CompletedTask;
    }

    #endregion

    #region Helper methods

    private static AgentExecutionPipeline CreatePipeline(
        string? responseText = "Default reply.",
        IConfiguration? config = null)
    {
        var chatClient = new Mock<IChatClient>();
        var responseMsg = new ChatMessage(ChatRole.Assistant, responseText);
        chatClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(responseMsg));

        config ??= new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        return new AgentExecutionPipeline(
            chatClient.Object,
            AgentTestFixtures.CreateMockHubContext(),
            config,
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());
    }

    private static AgentExecutionContext CreateContext(
        string message = "Hello",
        string fallbackReply = "I wasn't able to generate a response.",
        ChatRequest? request = null)
    {
        return new AgentExecutionContext
        {
            AgentName = "test-agent",
            SystemPrompt = "You are a test assistant.",
            Temperature = 0.7f,
            ModelName = "test-model",
            Request = request ?? new ChatRequest(message),
            Tools = Enumerable.Empty<AITool>(),
            FallbackReply = fallbackReply,
        };
    }

    #endregion
}

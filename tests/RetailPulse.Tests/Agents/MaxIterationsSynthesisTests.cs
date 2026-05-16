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
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Tests.Agents;

/// <summary>
/// Tests that validate the MaximumIterationsPerRequest setting's effect on
/// the agent execution pipeline. When MaxIterations >= 2, the LLM gets a second
/// turn after tool execution to synthesize results into text. When MaxIterations = 1,
/// the LLM never gets that second turn so response.Text is empty and the fallback fires.
///
/// These tests exist because MaxIterations was accidentally set to 1 (2026-05-16 incident),
/// causing ALL tool-using queries to return empty text in production.
/// </summary>
public class MaxIterationsSynthesisTests
{
    private const string TestQuery = "How is Apex Grill performing in the Southwest this quarter?";

    #region MaxIterations >= 2: LLM synthesizes after tool calls

    [Fact]
    public async Task Pipeline_WithMaxIterationsGte2_ProducesNonEmptyReply_WhenLlmSynthesizesAfterToolCalls()
    {
        // Simulates the production scenario: FunctionInvokingChatClient allows iteration 2
        // where the LLM sees tool results and produces a text response.
        // The pipeline receives a response with text content → reply is non-empty.
        const string synthesizedText = "Apex Grill showed 12% growth in the Southwest this quarter.";

        // The FunctionInvokingChatClient (with MaxIterations >= 2) calls the inner LLM twice:
        //   Iteration 1: LLM returns tool calls (no text)
        //   Iteration 2: LLM returns synthesized text
        // By the time the pipeline's _chatClient.GetResponseAsync() returns,
        // the middleware has already completed all iterations. The pipeline sees the FINAL response.
        // So we mock the final result: a response with text content.
        IChatClient chatClient = CreateMockChatClient(synthesizedText);
        AgentExecutionPipeline pipeline = CreatePipeline(chatClient);

        AgentExecutionContext context = CreateContext(TestQuery);
        ChatResponse response = await pipeline.ExecuteAsync(context);

        response.Should().NotBeNull();
        response.Reply.Should().NotBeNullOrWhiteSpace(
            "when MaxIterations >= 2, the LLM gets a second turn to synthesize tool results into text");
        response.Reply.Should().Contain("Apex Grill");
    }

    [Fact]
    public async Task Pipeline_WithMaxIterationsGte2_ReturnsLlmTextNotFallback()
    {
        // Verifies that when the LLM synthesizes text on iteration 2,
        // the pipeline uses that text — NOT the fallback reply.
        const string synthesizedText = "Coastline Tacos is seeing 8% YoY growth in the West Coast.";
        const string fallback = "I wasn't able to generate a response.";

        IChatClient chatClient = CreateMockChatClient(synthesizedText);
        AgentExecutionPipeline pipeline = CreatePipeline(chatClient);

        AgentExecutionContext context = CreateContext("How is Coastline Tacos doing?", fallback);
        ChatResponse response = await pipeline.ExecuteAsync(context);

        response.Reply.Should().Be(synthesizedText,
            "the pipeline must prefer LLM-synthesized text over the fallback when text is available");
        response.Reply.Should().NotBe(fallback);
    }

    #endregion

    #region MaxIterations = 1: Fallback fires (regression guard)

    [Fact]
    public async Task Pipeline_WhenResponseTextIsEmpty_FallbackFires_SimulatesMaxIterations1Behavior()
    {
        // When MaxIterations = 1, the FunctionInvokingChatClient calls the LLM once.
        // If the LLM decides to call tools, the middleware stops after that single iteration
        // WITHOUT giving the LLM a second turn to synthesize. The final response has tool
        // call content but NO text. The pipeline sees response.Text as null/empty.
        // This test simulates that scenario.
        const string fallback = "I wasn't able to generate a response.";

        IChatClient chatClient = CreateMockChatClient(null); // null text = MaxIterations exhausted after tool call
        AgentExecutionPipeline pipeline = CreatePipeline(chatClient);

        AgentExecutionContext context = CreateContext(TestQuery, fallback);
        ChatResponse response = await pipeline.ExecuteAsync(context);

        response.Reply.Should().NotBeNullOrWhiteSpace(
            "even when MaxIterations=1 causes empty LLM text, the fallback must prevent empty responses");
        response.Reply.Should().Be(fallback,
            "when LLM returns null text (MaxIterations exhausted), the pipeline uses the context FallbackReply");
    }

    [Fact]
    public async Task Pipeline_WhenResponseTextIsWhitespace_FallbackFires()
    {
        // Edge case: LLM returns whitespace-only text (equivalent to empty).
        // This can happen when MaxIterations is too low and the LLM emits only
        // formatting characters before being cut off.
        const string fallback = "Custom fallback for whitespace scenario.";

        IChatClient chatClient = CreateMockChatClient("   \n  ");
        AgentExecutionPipeline pipeline = CreatePipeline(chatClient);

        AgentExecutionContext context = CreateContext(TestQuery, fallback);
        ChatResponse response = await pipeline.ExecuteAsync(context);

        response.Reply.Should().NotBeNullOrWhiteSpace(
            "whitespace-only LLM responses must trigger the fallback, same as null");
    }

    #endregion

    #region Integration: FunctionInvokingChatClient with sequenced responses

    [Fact]
    public async Task FunctionInvokingClient_WithMaxIterations3_SynthesizesTextAfterToolExecution()
    {
        // Full integration test: uses a real FunctionInvokingChatClient to verify that
        // MaxIterations=3 allows the LLM to call a tool on turn 1 and synthesize on turn 2.
        // This is the closest test to production behavior without hitting a real LLM.
        const string synthesizedText = "Based on the data, Apex Grill grew 12% this quarter.";

        // Define a simple tool the LLM can call
        int toolCallCount = 0;
        AIFunction tool = AIFunctionFactory.Create(
            () =>
            {
                Interlocked.Increment(ref toolCallCount);
                return /*lang=json,strict*/ "{\"brand\":\"Apex Grill\",\"growth\":\"12%\",\"region\":\"Southwest\"}";
            },
            "GetBrandPerformance",
            "Gets brand performance data");

        // Inner LLM mock: first call returns tool call, second call returns synthesized text
        int callSequence = 0;
        Mock<IChatClient> innerClient = new();
        innerClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<ChatMessage> msgs, ChatOptions? opts, CancellationToken _) =>
            {
                int call = Interlocked.Increment(ref callSequence);
                return call == 1
                    ? new Microsoft.Extensions.AI.ChatResponse(
                        new ChatMessage(ChatRole.Assistant,
                        [
                            new FunctionCallContent("call_1", "GetBrandPerformance", new Dictionary<string, object?>())
                        ]))
                    : new Microsoft.Extensions.AI.ChatResponse(
                        new ChatMessage(ChatRole.Assistant, synthesizedText));
            });

        // Wrap with FunctionInvokingChatClient (MaxIterations=3, same as production)
        using var functionClient = new FunctionInvokingChatClient(innerClient.Object)
        {
            MaximumIterationsPerRequest = 3
        };

        // Build pipeline with the function-invoking client
        AgentExecutionPipeline pipeline = CreatePipeline(functionClient);

        AgentExecutionContext context = new()
        {
            AgentName = "test-agent",
            SystemPrompt = "You are a retail analytics assistant.",
            Temperature = 0.3f,
            ModelName = "test-model",
            Request = new ChatRequest(TestQuery, SessionId: "max-iter-integration"),
            Tools = [tool],
            FallbackReply = "I wasn't able to generate a response.",
        };

        ChatResponse response = await pipeline.ExecuteAsync(context);

        response.Reply.Should().NotBeNullOrWhiteSpace(
            "with MaxIterations=3, the LLM gets a second turn after tools to synthesize text");
        response.Reply.Should().Contain("Apex Grill");
        toolCallCount.Should().Be(1, "the tool should have been invoked exactly once");
        callSequence.Should().Be(2, "the inner LLM should have been called twice (tool call + synthesis)");
    }

    [Fact]
    public async Task FunctionInvokingClient_WithMaxIterations1_FallbackFires_BecauseLlmNeverSynthesizes()
    {
        // Regression guard: proves that MaxIterations=1 causes empty text when tools are called.
        // This is the exact bug that shipped — the LLM calls tools on iteration 1, but never
        // gets iteration 2 to synthesize results into text. response.Text ends up null.
        int toolCallCount = 0;
        AIFunction tool = AIFunctionFactory.Create(
            () =>
            {
                Interlocked.Increment(ref toolCallCount);
                return /*lang=json,strict*/ "{\"brand\":\"Apex Grill\",\"growth\":\"12%\"}";
            },
            "GetBrandPerformance",
            "Gets brand performance data");

        // Inner LLM: always returns a tool call (never gets to synthesize with MaxIterations=1)
        var innerClient = new Mock<IChatClient>();
        innerClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent("call_1", "GetBrandPerformance", new Dictionary<string, object?>())
                ])));

        // MaxIterations=1 — the exact broken configuration from the incident
        using var functionClient = new FunctionInvokingChatClient(innerClient.Object)
        {
            MaximumIterationsPerRequest = 1
        };

        AgentExecutionPipeline pipeline = CreatePipeline(functionClient);

        const string fallback = "I wasn't able to generate a response.";
        AgentExecutionContext context = new()
        {
            AgentName = "test-agent",
            SystemPrompt = "You are a retail analytics assistant.",
            Temperature = 0.3f,
            ModelName = "test-model",
            Request = new ChatRequest(TestQuery, SessionId: "max-iter-1-regression"),
            Tools = [tool],
            FallbackReply = fallback,
        };

        ChatResponse response = await pipeline.ExecuteAsync(context);

        // With MaxIterations=1, the tool executes but the LLM never gets to synthesize.
        // The pipeline sees no text content → fallback fires.
        response.Reply.Should().NotBeNullOrWhiteSpace(
            "the fallback must prevent a completely empty reply reaching the UI");
        toolCallCount.Should().Be(1,
            "the tool still executes on iteration 1, but the LLM never sees the result");
    }

    [Fact]
    public async Task FunctionInvokingClient_WithMaxIterations2_SynthesizesOnSecondIteration()
    {
        // Boundary test: MaxIterations=2 is the minimum needed for tool+synthesis.
        // Iteration 1: LLM calls tool. Iteration 2: LLM synthesizes.
        const string synthesizedText = "FreshMart is expanding rapidly in the Northeast.";

        int callSequence = 0;
        AIFunction tool = AIFunctionFactory.Create(
            () => /*lang=json,strict*/ "{\"brand\":\"FreshMart\",\"growth\":\"15%\"}",
            "GetBrandPerformance",
            "Gets brand performance data");

        Mock<IChatClient> innerClient = new();
        innerClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<ChatMessage> msgs, ChatOptions? opts, CancellationToken _) =>
            {
                int call = Interlocked.Increment(ref callSequence);
                return call == 1
                    ? new Microsoft.Extensions.AI.ChatResponse(
                        new ChatMessage(ChatRole.Assistant,
                        [
                            new FunctionCallContent("call_1", "GetBrandPerformance", new Dictionary<string, object?>())
                        ]))
                    : new Microsoft.Extensions.AI.ChatResponse(
                        new ChatMessage(ChatRole.Assistant, synthesizedText));
            });

        // MaxIterations=2 — minimum viable for tool + synthesis
        using var functionClient = new FunctionInvokingChatClient(innerClient.Object)
        {
            MaximumIterationsPerRequest = 2
        };

        AgentExecutionPipeline pipeline = CreatePipeline(functionClient);

        AgentExecutionContext context = new()
        {
            AgentName = "test-agent",
            SystemPrompt = "You are a retail analytics assistant.",
            Temperature = 0.3f,
            ModelName = "test-model",
            Request = new ChatRequest("How is FreshMart doing in the Northeast?", SessionId: "max-iter-2-boundary"),
            Tools = [tool],
            FallbackReply = "I wasn't able to generate a response.",
        };

        ChatResponse response = await pipeline.ExecuteAsync(context);

        response.Reply.Should().Be(synthesizedText,
            "MaxIterations=2 gives the LLM exactly one turn after tool execution to synthesize");
        callSequence.Should().Be(2);
    }

    #endregion

    #region Helpers

    private static IChatClient CreateMockChatClient(string? responseText)
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

    private static AgentExecutionPipeline CreatePipeline(IChatClient chatClient)
    {
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        return new AgentExecutionPipeline(
            chatClient,
            AgentTestFixtures.CreateMockHubContext(),
            config,
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());
    }

    private static AgentExecutionContext CreateContext(
        string message = "Hello",
        string fallbackReply = "I wasn't able to generate a response.")
    {
        return new AgentExecutionContext
        {
            AgentName = "test-agent",
            SystemPrompt = "You are a retail analytics assistant.",
            Temperature = 0.3f,
            ModelName = "test-model",
            Request = new ChatRequest(message, SessionId: "max-iter-test"),
            Tools = [],
            FallbackReply = fallbackReply,
        };
    }

    #endregion
}

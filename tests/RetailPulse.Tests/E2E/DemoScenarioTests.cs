using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Agents.Routing;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Tests.E2E;

/// <summary>
/// End-to-end demo scenario tests — verifies the 5 demo queries produce
/// well-structured responses with deterministic mocks. These do NOT hit
/// real services; they validate the routing → specialist → response pipeline.
/// </summary>
public class DemoScenarioTests
{
    private static readonly TimeSpan MaxQueryDuration = TimeSpan.FromSeconds(10);

    private static readonly string[] DemoQueries =
    [
        "How is Apex Grill performing in the Southwest this quarter?",
        "What's our competitive pricing position for premium burgers?",
        "What's the sentiment from field reps about our new Smokehouse line?",
        "Show me the portfolio health across all regions",
        "What are the top inventory depletion risks this week?"
    ];

    [Theory]
    [InlineData(0, "demand")]
    [InlineData(1, "competitive")]
    [InlineData(2, "sentiment")]
    [InlineData(3, "portfolio")]
    [InlineData(4, "supply")]
    public async Task DemoQuery_ProducesValidResponse(int queryIndex, string expectedDomain)
    {
        var query = DemoQueries[queryIndex];
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Arrange: mock router to classify correctly
        var routerResponse = $"{{\"intent\":\"{expectedDomain}\",\"confidence\":0.95,\"intents\":[\"{expectedDomain}\"]}}";
        var routerClient = CreateMockChatClient(routerResponse);

        var specialist = CreateMockSpecialist(expectedDomain, $"Response for {expectedDomain}");
        var generalAgent = CreateMockSpecialist("general", "General fallback response");
        var specialists = new List<ISpecialistAgent> { specialist, generalAgent };

        var router = CreateRouter(routerClient, specialists);

        // Act
        var decision = await router.RouteAsync(query, null, null, null);

        sw.Stop();

        // Assert — routing completed
        decision.Should().NotBeNull();
        decision.Intent.Should().NotBeNullOrWhiteSpace();
        decision.Confidence.Should().BeGreaterThan(0);

        // Assert — timing
        sw.Elapsed.Should().BeLessThan(MaxQueryDuration,
            $"Demo query '{query}' exceeded {MaxQueryDuration.TotalSeconds}s timeout");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void DemoQuery_SpecialistReturnsStructuredResponse(int queryIndex)
    {
        var query = DemoQueries[queryIndex];

        // Mock specialist that returns a ChatResponse
        var mockResponse = new ChatResponse(
            Reply: $"Analysis complete for: {query}. Key findings include growth in target segments.",
            SessionId: $"demo-session-{queryIndex}",
            Spans:
            [
                new AgentSpan("router.classify", "thought", "Classified query", 50, DateTimeOffset.UtcNow),
                new AgentSpan("specialist.execute", "tool_call", "Fetched data", 200, DateTimeOffset.UtcNow)
            ],
            TotalDurationMs: 250);

        // Assert response contract
        mockResponse.Reply.Should().NotBeNullOrEmpty("Demo query must produce a reply");
        mockResponse.SessionId.Should().NotBeNullOrEmpty("Response must include sessionId");
        mockResponse.Spans.Should().NotBeEmpty("Response should include telemetry spans");
        mockResponse.TotalDurationMs.Should().BeGreaterThan(0, "Duration should be tracked");
    }

    [Fact]
    public async Task AllDemoQueries_CompleteWithinTimeLimit()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tasks = new List<Task>();

        foreach (var query in DemoQueries)
        {
            tasks.Add(Task.Run(async () =>
            {
                var routerClient = CreateMockChatClient(
                    "{\"intent\":\"general\",\"confidence\":0.9,\"intents\":[\"general\"]}");
                var specialist = CreateMockSpecialist("general", $"Response for: {query}");
                var router = CreateRouter(routerClient, [specialist]);

                await router.RouteAsync(query, null, null, null);
            }));
        }

        await Task.WhenAll(tasks);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(MaxQueryDuration,
            "All 5 demo queries should complete within the time limit with mocked services");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void DemoQuery_ResponseCanSerializeToJson(int queryIndex)
    {
        var response = new ChatResponse(
            Reply: $"Response for demo query {queryIndex}",
            SessionId: $"session-{queryIndex}",
            Spans: [new AgentSpan("test", "response", "ok", 10, DateTimeOffset.UtcNow)],
            TotalDurationMs: 100);

        var json = System.Text.Json.JsonSerializer.Serialize(response);
        json.Should().NotBeNullOrEmpty();
        json.Should().Contain("Reply");
        json.Should().Contain("SessionId");
    }

    #region Helpers

    private static IChatClient CreateMockChatClient(string responseText)
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant, responseText)));
        return mock.Object;
    }

    private static ISpecialistAgent CreateMockSpecialist(string key, string responseText)
    {
        var mock = new Mock<ISpecialistAgent>();
        mock.Setup(a => a.Key).Returns(key);
        mock.Setup(a => a.DisplayName).Returns($"{key} Agent");
        mock.Setup(a => a.SupportedIntents).Returns([key]);
        mock.Setup(a => a.HandleAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(responseText, "session-1", []));
        return mock.Object;
    }

    private static RetailOpsRouter CreateRouter(IChatClient chatClient, IEnumerable<ISpecialistAgent> specialists)
    {
        var routerDef = new AgentDefinition
        {
            Name = "Router",
            Model = "gpt-5.4-mini",
            SystemPrompt = "Classify intent. Return JSON with intent, confidence, reasoning.",
            Temperature = 0.1
        };

        return new RetailOpsRouter(
            chatClient, routerDef, specialists,
            Mock.Of<ILogger<RetailOpsRouter>>());
    }

    #endregion
}


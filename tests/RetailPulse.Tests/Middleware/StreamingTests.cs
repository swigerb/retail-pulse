using FluentAssertions;
using RetailPulse.Api.Streaming;
using RetailPulse.Contracts.Streaming;

namespace RetailPulse.Tests.Middleware;

/// <summary>
/// Tests for streaming session delivery.
/// Covers: lifecycle events (start/token/complete/error), token ordering,
/// error handling, non-streaming fallback, session grouping.
/// </summary>
public class StreamingTests
{
    #region Start Event

    [Fact]
    public async Task Streaming_EmitsStartEvent_BeforeTokens()
    {
        var session = new InMemoryStreamingSession("session-1");

        await session.StreamResponseAsync("general", "Hello world");

        IReadOnlyList<StreamingEvent> events = session.Events;
        events.Should().HaveCountGreaterThan(1);
        events[0].Type.Should().Be("start");
    }

    [Fact]
    public async Task Streaming_StartEvent_HasCorrectSessionId()
    {
        var session = new InMemoryStreamingSession("session-42");
        await session.EmitStartAsync("general");

        session.Events.Should().ContainSingle();
        session.Events[0].SessionId.Should().Be("session-42");
    }

    #endregion

    #region Token Ordering

    [Fact]
    public async Task Streaming_TokensEmittedInOrder()
    {
        var session = new InMemoryStreamingSession("session-1");
        await session.StreamResponseAsync("general", "The quick brown fox");

        var tokenEvents = session.Events.Where(e => e.Type == "token").ToList();

        tokenEvents.Should().HaveCount(4);
        tokenEvents[0].Token.Should().Be("The");
        tokenEvents[1].Token.Should().Be("quick");
        tokenEvents[2].Token.Should().Be("brown");
        tokenEvents[3].Token.Should().Be("fox");
    }

    [Fact]
    public async Task Streaming_SequenceNumbers_AreMonotonicallyIncreasing()
    {
        var session = new InMemoryStreamingSession("session-1");
        await session.StreamResponseAsync("general", "one two three four five");

        var sequences = session.Events
            .Where(e => e.Type == "token")
            .Select(e => e.Sequence!.Value)
            .ToList();

        sequences.Should().BeInAscendingOrder();
        sequences.First().Should().Be(0);
    }

    [Fact]
    public async Task Streaming_SingleWordResponse_EmitsOneToken()
    {
        var session = new InMemoryStreamingSession("session-1");
        await session.StreamResponseAsync("general", "Hello");

        var tokenEvents = session.Events.Where(e => e.Type == "token").ToList();
        tokenEvents.Should().ContainSingle();
        tokenEvents[0].Token.Should().Be("Hello");
    }

    #endregion

    #region Complete Event

    [Fact]
    public async Task Streaming_CompleteEvent_FiredAfterLastToken()
    {
        var session = new InMemoryStreamingSession("session-1");
        await session.StreamResponseAsync("general", "Hello world");

        IReadOnlyList<StreamingEvent> events = session.Events;
        StreamingEvent? lastToken = events.LastOrDefault(e => e.Type == "token");
        StreamingEvent? complete = events.LastOrDefault(e => e.Type == "complete");

        complete.Should().NotBeNull();
        complete!.FullResponse.Should().Be("Hello world");

        var eventsList = events.ToList();
        int tokenIndex = eventsList.IndexOf(lastToken!);
        int completeIndex = eventsList.IndexOf(complete);
        completeIndex.Should().BeGreaterThan(tokenIndex, "complete must follow last token");
    }

    [Fact]
    public async Task Streaming_FullLifecycle_StartTokensComplete()
    {
        var session = new InMemoryStreamingSession("session-1");
        await session.StreamResponseAsync("general", "A B C");

        var types = session.Events.Select(e => e.Type).ToList();
        types.First().Should().Be("start");
        types.Last().Should().Be("complete");
        types.Skip(1).Take(types.Count - 2).Should().AllBe("token");
    }

    #endregion

    #region Error Handling

    [Fact]
    public async Task Streaming_ErrorEvent_OnFailure()
    {
        var session = new InMemoryStreamingSession("session-1");
        await session.EmitStartAsync("general");
        await session.EmitErrorAsync("LLM timeout: request failed");

        IReadOnlyList<StreamingEvent> events = session.Events;
        events.Should().HaveCount(2);
        events[1].Type.Should().Be("error");
        events[1].Error.Should().Contain("timeout");
    }

    [Fact]
    public async Task Streaming_ErrorEvent_DoesNotCrash()
    {
        var session = new InMemoryStreamingSession("session-1");

        // Error after partial tokens — should not throw
        await session.EmitStartAsync("general");
        await session.EmitTokenAsync("partial", 0);
        await session.EmitErrorAsync("Unexpected failure");

        session.Events.Count(e => e.Type == "error").Should().Be(1);
    }

    [Fact]
    public async Task Streaming_CancellationToken_StopsTokenEmission()
    {
        var session = new InMemoryStreamingSession("session-1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => session.StreamResponseAsync("general", "A B C D E", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region Non-Streaming Fallback

    [Fact]
    public void NonStreamingFallback_ReturnsFullResponse()
    {
        string response = "This is the complete response from the agent.";
        string result = InMemoryStreamingSession.GetNonStreamingResponse(response);

        result.Should().Be(response);
    }

    [Fact]
    public void NonStreamingFallback_EmptyResponse_ReturnsEmpty()
    {
        string result = InMemoryStreamingSession.GetNonStreamingResponse("");
        result.Should().BeEmpty();
    }

    #endregion

    #region Session Grouping

    [Fact]
    public async Task SessionGrouping_DifferentSessions_IndependentEvents()
    {
        var session1 = new InMemoryStreamingSession("session-1");
        var session2 = new InMemoryStreamingSession("session-2");

        await session1.StreamResponseAsync("general", "Hello");
        await session2.StreamResponseAsync("general", "World");

        session1.Events.All(e => e.SessionId == "session-1").Should().BeTrue();
        session2.Events.All(e => e.SessionId == "session-2").Should().BeTrue();
    }

    [Fact]
    public async Task SessionGrouping_OnlySubscribers_GetEvents()
    {
        var session1 = new InMemoryStreamingSession("session-1");
        var session2 = new InMemoryStreamingSession("session-2");

        await session1.EmitTokenAsync("token-for-1", 0);

        session1.Events.Should().ContainSingle();
        session2.Events.Should().BeEmpty("session-2 should not see session-1 events");
    }

    [Fact]
    public async Task SessionGrouping_SessionId_IsImmutable()
    {
        var session = new InMemoryStreamingSession("fixed-id");
        await session.EmitStartAsync("general");

        session.SessionId.Should().Be("fixed-id");
    }

    #endregion
}

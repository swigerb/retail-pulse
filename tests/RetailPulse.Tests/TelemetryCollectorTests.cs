using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Moq;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Middleware;
using RetailPulse.Contracts;

namespace RetailPulse.Tests;

public class TelemetryCollectorTests
{
    private readonly TelemetryCollector _collector;

    public TelemetryCollectorTests()
    {
        var mockHubContext = new Mock<IHubContext<TelemetryHub>>();
        // No sessionId is supplied — collector should accumulate spans
        // locally without pushing to any SignalR group.
        _collector = new TelemetryCollector(mockHubContext.Object);
    }

    [Fact]
    public async Task RecordSpanAsync_AddsSpanToCollection()
    {
        await _collector.RecordSpanAsync("test-span", "thought", "thinking...", 42.5);

        var spans = _collector.Spans.ToList();
        spans.Should().HaveCount(1);
        spans[0].Name.Should().Be("test-span");
        spans[0].Type.Should().Be("thought");
        spans[0].Detail.Should().Be("thinking...");
        spans[0].DurationMs.Should().Be(42.5);
    }

    [Fact]
    public async Task RecordSpanAsync_CreatesCorrectAgentSpan()
    {
        var before = DateTimeOffset.UtcNow;
        await _collector.RecordSpanAsync("agent.response", "response", "Hello!", 100.0);
        var after = DateTimeOffset.UtcNow;

        var span = _collector.Spans.First();
        span.Should().BeOfType<AgentSpan>();
        span.Timestamp.Should().BeOnOrAfter(before);
        span.Timestamp.Should().BeOnOrBefore(after);
    }

    [Fact]
    public async Task MultipleSpans_AreOrderedCorrectly()
    {
        await _collector.RecordSpanAsync("span-1", "thought", "first", 10.0);
        await _collector.RecordSpanAsync("span-2", "tool_call", "second", 20.0);
        await _collector.RecordSpanAsync("span-3", "response", "third", 30.0);

        var spans = _collector.Spans.ToList();
        spans.Should().HaveCount(3);
        spans[0].Name.Should().Be("span-1");
        spans[1].Name.Should().Be("span-2");
        spans[2].Name.Should().Be("span-3");
    }

    [Fact]
    public async Task RecordSpanAsync_WithSessionId_PushesToSignalRGroup()
    {
        const string sessionId = "session-123";
        var mockHubContext = new Mock<IHubContext<TelemetryHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockGroupProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.Group(sessionId)).Returns(mockGroupProxy.Object);
        mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        var collector = new TelemetryCollector(mockHubContext.Object, sessionId);
        await collector.RecordSpanAsync("test", "thought", "detail", 5.0);

        mockGroupProxy.Verify(
            x => x.SendCoreAsync("SpanReceived", It.IsAny<object?[]>(), default),
            Times.Once);
    }

    [Fact]
    public async Task RecordSpanAsync_WithoutSessionId_DoesNotPushToSignalR()
    {
        var mockHubContext = new Mock<IHubContext<TelemetryHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockGroupProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockGroupProxy.Object);
        mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        var collector = new TelemetryCollector(mockHubContext.Object);
        await collector.RecordSpanAsync("test", "thought", "detail", 5.0);

        mockGroupProxy.Verify(
            x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), default),
            Times.Never);
    }
}

using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Moq;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Models;

namespace RetailPulse.Tests;

public class TelemetryCollectorTests
{
    private readonly TelemetryCollector _collector;

    public TelemetryCollectorTests()
    {
        var mockHubContext = new Mock<IHubContext<TelemetryHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);
        mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);
        _collector = new TelemetryCollector(mockHubContext.Object);
    }

    [Fact]
    public async Task RecordSpanAsync_AddsSpanToCollection()
    {
        await _collector.RecordSpanAsync("test-span", "thought", "thinking...", 42.5);

        _collector.Spans.Should().HaveCount(1);
        _collector.Spans[0].Name.Should().Be("test-span");
        _collector.Spans[0].Type.Should().Be("thought");
        _collector.Spans[0].Detail.Should().Be("thinking...");
        _collector.Spans[0].DurationMs.Should().Be(42.5);
    }

    [Fact]
    public async Task RecordSpanAsync_CreatesCorrectAgentSpan()
    {
        var before = DateTimeOffset.UtcNow;
        await _collector.RecordSpanAsync("agent.response", "response", "Hello!", 100.0);
        var after = DateTimeOffset.UtcNow;

        var span = _collector.Spans[0];
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

        _collector.Spans.Should().HaveCount(3);
        _collector.Spans[0].Name.Should().Be("span-1");
        _collector.Spans[1].Name.Should().Be("span-2");
        _collector.Spans[2].Name.Should().Be("span-3");
    }

    [Fact]
    public async Task RecordSpanAsync_PushesToSignalRClients()
    {
        var mockHubContext = new Mock<IHubContext<TelemetryHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);
        mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        var collector = new TelemetryCollector(mockHubContext.Object);
        await collector.RecordSpanAsync("test", "thought", "detail", 5.0);

        mockClientProxy.Verify(
            x => x.SendCoreAsync("SpanReceived", It.IsAny<object?[]>(), default),
            Times.Once);
    }
}

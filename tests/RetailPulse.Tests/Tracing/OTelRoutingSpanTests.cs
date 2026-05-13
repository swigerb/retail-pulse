using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Agents.Routing;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Models;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Tests.Tracing;

/// <summary>
/// Tests that the RetailOpsRouter emits OTel spans with the correct
/// intent, confidence, and fallback attributes on the "agent.routing" activity.
/// Act 6 coverage gap #3.
/// </summary>
[Collection("OTel")]
public class OTelRoutingSpanTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _capturedActivities = new();

    public OTelRoutingSpanTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "RetailPulse.Agent",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _capturedActivities.Add(activity),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        GC.SuppressFinalize(this);
    }

    private static RetailOpsRouter CreateRouter(IChatClient routerClient, IEnumerable<ISpecialistAgent>? specialists = null)
    {
        var agentDef = new AgentDefinition
        {
            Name = "router",
            SystemPrompt = "Classify the user's intent.",
            Temperature = 0.1
        };

        var specList = specialists ?? Array.Empty<ISpecialistAgent>();
        return new RetailOpsRouter(
            routerClient, agentDef, specList,
            new Mock<ILogger<RetailOpsRouter>>().Object);
    }

    private static IChatClient MockChatClient(string responseJson)
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseJson)));
        return mock.Object;
    }

    #region Routing Span Tags

    [Fact]
    public async Task RoutingSpan_EmitsIntentTag()
    {
        var client = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.DemandForecasting}\",\"confidence\":0.92}}");
        var router = CreateRouter(client);

        await router.RouteAsync("What is the demand forecast?", null, null, null);

        var routingActivity = _capturedActivities
            .LastOrDefault(a => a.OperationName == "agent.routing"
                && a.GetTagItem("agent.router") as string == "RetailOpsRouter");
        routingActivity.Should().NotBeNull("router should emit an agent.routing span");

        var intentTag = routingActivity!.GetTagItem("agent.routing.intent");
        intentTag.Should().Be(AgentIntent.DemandForecasting);
    }

    [Fact]
    public async Task RoutingSpan_EmitsConfidenceTag()
    {
        var client = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.CompetitiveMarket}\",\"confidence\":0.87}}");
        var router = CreateRouter(client);

        await router.RouteAsync("How is the competitive landscape?", null, null, null);

        var routingActivity = _capturedActivities
            .LastOrDefault(a => a.OperationName == "agent.routing"
                && a.GetTagItem("agent.router") as string == "RetailOpsRouter");
        routingActivity.Should().NotBeNull();

        var confidenceTag = routingActivity!.GetTagItem("agent.routing.confidence");
        confidenceTag.Should().NotBeNull("routing span should include confidence tag");
        Convert.ToDouble(confidenceTag).Should().BeApproximately(0.87, 0.01);
    }

    [Fact]
    public async Task RoutingSpan_EmitsFallbackTag_WhenLowConfidence()
    {
        var client = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.SupplyShipments}\",\"confidence\":0.3}}");

        // Need a general agent registered for fallback
        var generalAgent = new Mock<ISpecialistAgent>();
        generalAgent.Setup(a => a.Key).Returns("general");
        generalAgent.Setup(a => a.SupportedIntents).Returns(new[] { AgentIntent.General });

        var router = CreateRouter(client, new[] { generalAgent.Object });

        await router.RouteAsync("Something vague", null, null, null);

        var routingActivity = _capturedActivities
            .LastOrDefault(a => a.OperationName == "agent.routing"
                && a.GetTagItem("agent.router") as string == "RetailOpsRouter");
        routingActivity.Should().NotBeNull();

        var fallbackTag = routingActivity!.GetTagItem("agent.routing.fallback");
        fallbackTag.Should().Be(true, "low confidence should set fallback=true");

        var fallbackReason = routingActivity.GetTagItem("agent.routing.fallback_reason");
        fallbackReason.Should().Be("low_confidence");
    }

    [Fact]
    public async Task RoutingSpan_NoFallbackTag_WhenHighConfidence()
    {
        var client = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.DemandForecasting}\",\"confidence\":0.95}}");

        var demandAgent = new Mock<ISpecialistAgent>();
        demandAgent.Setup(a => a.Key).Returns("demand-forecasting");
        demandAgent.Setup(a => a.SupportedIntents).Returns(new[] { AgentIntent.DemandForecasting });

        var router = CreateRouter(client, new[] { demandAgent.Object });

        await router.RouteAsync("What is the demand forecast?", null, null, null);

        var routingActivity = _capturedActivities
            .LastOrDefault(a => a.OperationName == "agent.routing"
                && a.GetTagItem("agent.router") as string == "RetailOpsRouter");
        routingActivity.Should().NotBeNull();

        // High confidence → no fallback tag set (or false)
        var fallbackTag = routingActivity!.GetTagItem("agent.routing.fallback");
        fallbackTag.Should().BeNull("high confidence routes should not set fallback tag");
    }

    [Fact]
    public async Task RoutingSpan_EmitsFallbackTag_WhenNoSpecialist()
    {
        // Return a valid intent but register no specialists
        var client = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.DemandForecasting}\",\"confidence\":0.95}}");
        var router = CreateRouter(client); // no specialists registered

        await router.RouteAsync("Demand forecast?", null, null, null);

        var routingActivity = _capturedActivities
            .LastOrDefault(a => a.OperationName == "agent.routing"
                && a.GetTagItem("agent.router") as string == "RetailOpsRouter");
        routingActivity.Should().NotBeNull();

        var fallbackTag = routingActivity!.GetTagItem("agent.routing.fallback");
        fallbackTag.Should().Be(true, "missing specialist should trigger fallback");

        var fallbackReason = routingActivity.GetTagItem("agent.routing.fallback_reason");
        fallbackReason.Should().Be("no_specialist");
    }

    #endregion
}

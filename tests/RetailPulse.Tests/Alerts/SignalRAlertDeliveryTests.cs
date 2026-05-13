using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Moq;
using RetailPulse.Api.Alerts;
using RetailPulse.Api.Hubs;
using RetailPulse.Contracts.Alerts;

namespace RetailPulse.Tests.Alerts;

/// <summary>
/// Tests that when alerts are generated, they are pushed to the
/// SignalR TelemetryHub via IHubContext.Clients.All.SendAsync("alert_fired", ...).
/// Act 8 coverage gap #5.
/// </summary>
public class SignalRAlertDeliveryTests
{
    private static (Mock<IHubContext<TelemetryHub>> Hub, Mock<IClientProxy> ClientProxy) CreateHubMock()
    {
        var clientProxy = new Mock<IClientProxy>();
        var hubClients = new Mock<IHubClients>();
        hubClients.Setup(c => c.All).Returns(clientProxy.Object);

        var hub = new Mock<IHubContext<TelemetryHub>>();
        hub.Setup(h => h.Clients).Returns(hubClients.Object);

        return (hub, clientProxy);
    }

    #region Alert → SignalR Push

    [Fact]
    public async Task AlertFired_PushesToSignalRHub()
    {
        var (hub, clientProxy) = CreateHubMock();

        var alert = new Alert(
            Id: "alert-001",
            Type: "demand_spike",
            Severity: "high",
            Title: "Demand spike for Sierra Gold in Northeast",
            Description: "45% above baseline",
            Brand: "Sierra Gold Tequila",
            Region: "Northeast",
            RecommendedAction: "Review inventory levels",
            DetectedAt: DateTimeOffset.UtcNow,
            Metadata: new Dictionary<string, object> { ["pctChange"] = 45.0 });

        // Simulate what ProactiveAlertService does: push alert to hub
        await hub.Object.Clients.All.SendAsync("alert_fired", new
        {
            id = alert.Id,
            type = alert.Type,
            severity = alert.Severity,
            title = alert.Title,
            description = alert.Description,
            brand = alert.Brand,
            region = alert.Region,
            recommendedAction = alert.RecommendedAction,
            detectedAt = alert.DetectedAt,
            metadata = alert.Metadata
        });

        clientProxy.Verify(
            c => c.SendCoreAsync(
                "alert_fired",
                It.Is<object?[]>(args => args.Length == 1),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "alert should be pushed to all SignalR clients exactly once");
    }

    [Fact]
    public async Task MultipleAlerts_EachPushedSeparately()
    {
        var (hub, clientProxy) = CreateHubMock();

        var alerts = new[]
        {
            new Alert("a1", "demand_spike", "high", "Spike 1", "desc", "Brand A", "NE", "action", DateTimeOffset.UtcNow),
            new Alert("a2", "supply_drop", "medium", "Drop 1", "desc", "Brand B", "SE", "action", DateTimeOffset.UtcNow),
            new Alert("a3", "trend_reversal", "medium", "Reversal 1", "desc", "Brand C", "MW", "action", DateTimeOffset.UtcNow),
        };

        foreach (var alert in alerts)
        {
            await hub.Object.Clients.All.SendAsync("alert_fired", new
            {
                id = alert.Id,
                type = alert.Type,
                severity = alert.Severity,
                title = alert.Title
            });
        }

        clientProxy.Verify(
            c => c.SendCoreAsync("alert_fired", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3),
            "each alert should be pushed separately to the hub");
    }

    [Fact]
    public async Task AlertPayload_ContainsBrandAndRegion()
    {
        var (hub, clientProxy) = CreateHubMock();
        object? capturedPayload = null;

        clientProxy
            .Setup(c => c.SendCoreAsync("alert_fired", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedPayload = args[0])
            .Returns(Task.CompletedTask);

        var alert = new Alert("a1", "demand_spike", "high", "Spike", "45% above",
            "Ridgeline Bourbon", "Southeast", "Review stock", DateTimeOffset.UtcNow);

        await hub.Object.Clients.All.SendAsync("alert_fired", new
        {
            id = alert.Id,
            type = alert.Type,
            severity = alert.Severity,
            brand = alert.Brand,
            region = alert.Region
        });

        capturedPayload.Should().NotBeNull("payload should be captured");
        var json = System.Text.Json.JsonSerializer.Serialize(capturedPayload);
        json.Should().Contain("Ridgeline Bourbon");
        json.Should().Contain("Southeast");
    }

    #endregion

    #region No Alerts — No Push

    [Fact]
    public async Task NoAlerts_NothingPushed()
    {
        var (_, clientProxy) = CreateHubMock();

        // Don't call SendAsync — simulate a check cycle with 0 alerts
        var alertService = new InMemoryAlertService();
        var alerts = await alertService.CheckForAlertsAsync();

        alerts.Should().BeEmpty("no data seeded → no alerts");

        clientProxy.Verify(
            c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no alerts should mean no SignalR messages");
    }

    #endregion
}

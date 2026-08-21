using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RetailPulse.Api.Hubs;

namespace RetailPulse.Tests.Hubs;

/// <summary>
/// Backend contract for issue #92: an application-level heartbeat is emitted on
/// both hubs at the configured cadence, and the emitter honours the
/// configuration binding rather than a private hard-coded default.
/// </summary>
public sealed class HubHeartbeatBackgroundServiceTests
{
    private static (Mock<IHubContext<T>> Hub, Mock<IClientProxy> Proxy) NewHub<T>() where T : Hub
    {
        var proxy = new Mock<IClientProxy>();
        var clients = new Mock<IHubClients>();
        clients.SetupGet(c => c.All).Returns(proxy.Object);
        var hub = new Mock<IHubContext<T>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);
        return (hub, proxy);
    }

    [Fact]
    public async Task EmitOnce_SendsHeartbeat_ToBothHubs()
    {
        (Mock<IHubContext<TelemetryHub>> telemetry, Mock<IClientProxy> telemetryProxy) = NewHub<TelemetryHub>();
        (Mock<IHubContext<StreamingHub>> streaming, Mock<IClientProxy> streamingProxy) = NewHub<StreamingHub>();

        IOptions<RealtimeResilienceOptions> options = Options.Create(new RealtimeResilienceOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15),
            ClientTimeoutInterval = TimeSpan.FromSeconds(30),
            ApplicationHeartbeatInterval = TimeSpan.FromMilliseconds(50),
            ApplicationHeartbeatEnabled = true,
        });

        var svc = new HubHeartbeatBackgroundService(
            telemetry.Object,
            streaming.Object,
            options,
            TimeProvider.System,
            NullLogger<HubHeartbeatBackgroundService>.Instance);

        await svc.EmitOnceAsync(CancellationToken.None);

        telemetryProxy.Verify(p => p.SendCoreAsync(
            HubHeartbeatBackgroundService.EventName,
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
        streamingProxy.Verify(p => p.SendCoreAsync(
            HubHeartbeatBackgroundService.EventName,
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
        svc.EmittedCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_EmitsAtConfiguredInterval()
    {
        (Mock<IHubContext<TelemetryHub>> telemetry, _) = NewHub<TelemetryHub>();
        (Mock<IHubContext<StreamingHub>> streaming, _) = NewHub<StreamingHub>();

        IOptions<RealtimeResilienceOptions> options = Options.Create(new RealtimeResilienceOptions
        {
            ApplicationHeartbeatInterval = TimeSpan.FromMilliseconds(40),
            ApplicationHeartbeatEnabled = true,
        });

        var svc = new HubHeartbeatBackgroundService(
            telemetry.Object,
            streaming.Object,
            options,
            TimeProvider.System,
            NullLogger<HubHeartbeatBackgroundService>.Instance);

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        // Wait a real interval so the PeriodicTimer fires several times.
        await Task.Delay(TimeSpan.FromMilliseconds(250));

        cts.Cancel();
        await svc.StopAsync(CancellationToken.None);

        svc.EmittedCount.Should().BeGreaterThanOrEqualTo(3,
            "the emitter should tick at the configured cadence, not a hard-coded default");
        svc.Interval.Should().Be(TimeSpan.FromMilliseconds(40));
    }

    [Fact]
    public async Task ExecuteAsync_DisabledByConfig_DoesNotEmit()
    {
        (Mock<IHubContext<TelemetryHub>> telemetry, Mock<IClientProxy> telemetryProxy) = NewHub<TelemetryHub>();
        (Mock<IHubContext<StreamingHub>> streaming, Mock<IClientProxy> streamingProxy) = NewHub<StreamingHub>();

        IOptions<RealtimeResilienceOptions> options = Options.Create(new RealtimeResilienceOptions
        {
            ApplicationHeartbeatInterval = TimeSpan.FromMilliseconds(20),
            ApplicationHeartbeatEnabled = false,
        });

        var svc = new HubHeartbeatBackgroundService(
            telemetry.Object,
            streaming.Object,
            options,
            TimeProvider.System,
            NullLogger<HubHeartbeatBackgroundService>.Instance);

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        await svc.StopAsync(CancellationToken.None);

        svc.EmittedCount.Should().Be(0);
        telemetryProxy.Verify(p => p.SendCoreAsync(
            It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

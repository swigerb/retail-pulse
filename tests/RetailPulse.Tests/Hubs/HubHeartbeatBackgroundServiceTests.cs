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

        // A cadence unlikely to collide with any hard-coded default. The service
        // MUST call CreateTimer with this exact period; if it silently falls back
        // to a hard-coded default, no ManualTimer with a 40ms period will exist
        // and Advance(40ms) will not produce a tick — EmittedCount stays at 0.
        var configured = TimeSpan.FromMilliseconds(40);

        IOptions<RealtimeResilienceOptions> options = Options.Create(new RealtimeResilienceOptions
        {
            ApplicationHeartbeatInterval = configured,
            ApplicationHeartbeatEnabled = true,
        });

        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

        var svc = new HubHeartbeatBackgroundService(
            telemetry.Object,
            streaming.Object,
            options,
            clock,
            NullLogger<HubHeartbeatBackgroundService>.Instance);

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        // Wait for ExecuteAsync to register its PeriodicTimer with the clock so
        // the first Advance actually finds a timer to tick. Polls logical state,
        // not wall time.
        bool registered = await WaitUntilAsync(() => clock.TimerCount >= 1, TimeSpan.FromSeconds(5));
        registered.Should().BeTrue("ExecuteAsync must create a PeriodicTimer from the injected TimeProvider");

        // Advance logical time exactly three periods and wait for the emitter to
        // observe each tick. Because the service registers a timer with period ==
        // configured, an Advance of `configured` must produce exactly one tick.
        // Under a hard-coded default (say 30s), the same Advance produces zero
        // ticks, so the assertion below still fails deterministically.
        for (int i = 1; i <= 3; i++)
        {
            long snapshot = svc.EmittedCount;
            clock.Advance(configured);
            bool ticked = await WaitUntilAsync(() => svc.EmittedCount > snapshot, TimeSpan.FromSeconds(5));
            ticked.Should().BeTrue(
                $"tick {i} must land after advancing logical time by the configured interval");
        }

        cts.Cancel();
        await svc.StopAsync(CancellationToken.None);

        svc.EmittedCount.Should().BeGreaterThanOrEqualTo(3,
            "the emitter should tick at the configured cadence, not a hard-coded default");
        svc.Interval.Should().Be(configured);
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

        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

        var svc = new HubHeartbeatBackgroundService(
            telemetry.Object,
            streaming.Object,
            options,
            clock,
            NullLogger<HubHeartbeatBackgroundService>.Instance);

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        // With emission disabled the service must return before creating a timer.
        // Advance far beyond any plausible cadence — a hard-coded default that
        // ignored the disabled flag would tick many times here.
        clock.Advance(TimeSpan.FromSeconds(10));

        await svc.StopAsync(CancellationToken.None);

        clock.TimerCount.Should().Be(0, "disabled config must short-circuit before creating a PeriodicTimer");
        svc.EmittedCount.Should().Be(0);
        telemetryProxy.Verify(p => p.SendCoreAsync(
            It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    /// <summary>
    /// Deterministic <see cref="TimeProvider"/> mirroring the pattern established
    /// by the persistence sweep tests introduced in #90/#91: <see cref="Advance"/>
    /// moves logical time forward and fires any registered <see cref="PeriodicTimer"/>
    /// whose period has elapsed. Lets the heartbeat test assert on logical ticks
    /// rather than wall-clock elapsed time.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        private readonly List<ManualTimer> _timers = [];
        private readonly Lock _lock = new();

        public ManualTimeProvider(DateTimeOffset start) { _now = start; }

        public int TimerCount
        {
            get { lock (_lock) return _timers.Count; }
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            lock (_lock) _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan delta)
        {
            List<ManualTimer> snapshot;
            lock (_lock)
            {
                _now += delta;
                snapshot = [.. _timers];
            }
            foreach (ManualTimer t in snapshot) t.Tick(delta);
        }

        internal void Remove(ManualTimer t)
        {
            lock (_lock) _timers.Remove(t);
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _provider;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _dueTime;
        private TimeSpan _period;
        private TimeSpan _accum;
        private bool _disposed;

        public ManualTimer(ManualTimeProvider provider, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _provider = provider;
            _callback = callback;
            _state = state;
            _dueTime = dueTime;
            _period = period;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _dueTime = dueTime;
            _period = period;
            _accum = TimeSpan.Zero;
            return true;
        }

        public void Tick(TimeSpan delta)
        {
            if (_disposed) return;
            _accum += delta;
            while (!_disposed && _accum >= _dueTime && _dueTime > TimeSpan.Zero)
            {
                _accum -= _dueTime;
                _dueTime = _period > TimeSpan.Zero ? _period : Timeout.InfiniteTimeSpan;
                _callback(_state);
            }
        }

        public void Dispose() { _disposed = true; _provider.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

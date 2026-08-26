using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using RetailPulse.Api.Persistence;
using RetailPulse.Contracts.Persistence;

namespace RetailPulse.Tests.Persistence;

/// <summary>
/// Retention/observability tests for <see cref="SessionCleanupBackgroundService"/>.
/// Uses a hand-rolled <see cref="TimeProvider"/> so the sweep cadence is driven by
/// deterministic ticks, not <see cref="Task.Delay"/> — matching the issue #90
/// mandate to use a clock abstraction rather than sleeps. We only need to run
/// enough time forward to trigger one <see cref="ISessionStore.PurgeExpiredAsync"/>
/// call and observe that the reported result surfaces in the logs.
/// </summary>
public sealed class SessionCleanupBackgroundServiceTests
{
    [Fact]
    public async Task PurgesExpiredSessions_OnClockTick_AndLogsRowCounts()
    {
        var store = new Mock<ISessionStore>(MockBehavior.Strict);
        // Record EVERY cutoff, not just the first. The service sweeps once
        // immediately on start and again on each timer tick, so capturing only the
        // first observation raced the clock: whichever of the two sweeps happened to
        // land first decided the assertion. Recording all of them lets the test pin
        // each sweep to its own point on the injected clock.
        var seenCutoffs = new System.Collections.Concurrent.ConcurrentQueue<DateTimeOffset>();
        store.Setup(s => s.PurgeExpiredAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<DateTimeOffset, CancellationToken>((cutoff, _) => seenCutoffs.Enqueue(cutoff))
            .ReturnsAsync(new CleanupResult(SessionsDeleted: 3, TurnsDeleted: 12));

        IOptions<SessionPersistenceOptions> options = Options.Create(new SessionPersistenceOptions
        {
            Enabled = true,
            RetentionTtl = TimeSpan.FromDays(30),
            CleanupInterval = TimeSpan.FromMinutes(15),
        });

        DateTimeOffset now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var logger = new CountingLogger<SessionCleanupBackgroundService>();

        var svc = new SessionCleanupBackgroundService(store.Object, options, clock, logger);

        using var cts = new CancellationTokenSource();
        Task loop = svc.StartAsync(cts.Token);

        // The service sweeps immediately on start, before waiting on the timer. Let
        // that first sweep settle BEFORE advancing the clock, so the tick-driven
        // sweep is unambiguously the second observation.
        bool startupSwept = await WaitUntilAsync(() => seenCutoffs.Count >= 1, TimeSpan.FromSeconds(5));
        startupSwept.Should().BeTrue("the service must sweep once on start, before the first interval elapses");

        seenCutoffs.TryPeek(out DateTimeOffset startupCutoff).Should().BeTrue();
        startupCutoff.Should().Be(now - options.Value.RetentionTtl,
            "the startup sweep's cutoff must be (injected-now - RetentionTtl)");

        // One tick of the CleanupInterval → PeriodicTimer fires → a second sweep runs.
        // Poll rather than sleep so the test doesn't race the timer's own scheduling.
        clock.Advance(options.Value.CleanupInterval);
        bool purged = await WaitUntilAsync(() => seenCutoffs.Count >= 2, TimeSpan.FromSeconds(5));
        purged.Should().BeTrue("the first tick after CleanupInterval must trigger another purge sweep");

        // The tick sweep reads GetUtcNow() at tick time, which is now + CleanupInterval.
        // The cutoff must therefore reflect the injected clock at THAT moment, not the
        // wall clock — the whole point of the TimeProvider abstraction.
        DateTimeOffset expectedCutoff = now + options.Value.CleanupInterval - options.Value.RetentionTtl;
        DateTimeOffset[] cutoffs = [.. seenCutoffs];
        cutoffs[1].Should().Be(expectedCutoff,
            "the cutoff must be exactly (injected-now - RetentionTtl) so the injected clock — not the wall clock — drives retention");

        bool logged = await WaitUntilAsync(() => logger.InformationCount >= 2, TimeSpan.FromSeconds(5));
        logged.Should().BeTrue("cleanup start + swept counts must both be logged for retention observability");

        await cts.CancelAsync();
        await svc.StopAsync(CancellationToken.None);
        await loop;
    }

    [Fact]
    public async Task ExitsWithoutSweeping_When_RetentionOrIntervalIsNonPositive()
    {
        var store = new Mock<ISessionStore>(MockBehavior.Strict);
        IOptions<SessionPersistenceOptions> options = Options.Create(new SessionPersistenceOptions
        {
            Enabled = true,
            RetentionTtl = TimeSpan.Zero,
            CleanupInterval = TimeSpan.FromMinutes(15),
        });

        var svc = new SessionCleanupBackgroundService(
            store.Object, options, TimeProvider.System, Mock.Of<ILogger<SessionCleanupBackgroundService>>());

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);
        await svc.StopAsync(CancellationToken.None);

        // No purge call must have been made — the sweeper turns itself off when
        // retention is unbounded, so the operator gets durable writes without a
        // deletion cadence they didn't ask for.
        store.Verify(
            s => s.PurgeExpiredAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    /// <summary>
    /// Small deterministic <see cref="TimeProvider"/> used to drive the cleanup
    /// service's <see cref="PeriodicTimer"/> without a wall-clock dependency.
    /// We only need <see cref="CreateTimer"/> semantics rich enough for
    /// <see cref="PeriodicTimer.WaitForNextTickAsync"/> to fire once per
    /// <see cref="Advance"/> call whose delta covers the timer's period.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        private readonly List<ManualTimer> _timers = [];
        private readonly Lock _lock = new();

        public ManualTimeProvider(DateTimeOffset start) { _now = start; }

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

    private sealed class CountingLogger<T> : ILogger<T>
    {
        public int InformationCount;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information) Interlocked.Increment(ref InformationCount);
        }
    }
}

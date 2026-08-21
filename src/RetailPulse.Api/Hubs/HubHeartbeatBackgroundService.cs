using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace RetailPulse.Api.Hubs;

/// <summary>
/// Emits an observable application-level heartbeat to both the telemetry and
/// streaming hubs at <see cref="RealtimeResilienceOptions.ApplicationHeartbeatInterval"/>.
///
/// <para>Why this exists in addition to the SignalR keep-alive: the transport
/// keep-alive is invisible to the browser layer and cannot be asserted by a
/// backend test. An application-level heartbeat is a real hub message on the
/// same channel clients already subscribe to, so the frontend has a signal it
/// can render ("connected" vs "stalled") and tests can assert emission
/// cadence without spinning up Kestrel or SignalR's negotiate pipeline.</para>
///
/// <para>Emission is best-effort: an <see cref="OperationCanceledException"/>
/// during shutdown is expected; any other failure is logged and swallowed so a
/// transient hub error cannot crash the host. The counters are exposed for
/// tests to assert cadence deterministically.</para>
/// </summary>
public sealed class HubHeartbeatBackgroundService : BackgroundService
{
    /// <summary>Event name emitted on both hubs.</summary>
    public const string EventName = "heartbeat";

    private readonly IHubContext<TelemetryHub> _telemetryHub;
    private readonly IHubContext<StreamingHub> _streamingHub;
    private readonly RealtimeResilienceOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HubHeartbeatBackgroundService> _logger;

    private long _emittedCount;

    public HubHeartbeatBackgroundService(
        IHubContext<TelemetryHub> telemetryHub,
        IHubContext<StreamingHub> streamingHub,
        IOptions<RealtimeResilienceOptions> options,
        TimeProvider timeProvider,
        ILogger<HubHeartbeatBackgroundService> logger)
    {
        _telemetryHub = telemetryHub ?? throw new ArgumentNullException(nameof(telemetryHub));
        _streamingHub = streamingHub ?? throw new ArgumentNullException(nameof(streamingHub));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Total heartbeats successfully emitted since process start (test hook).</summary>
    public long EmittedCount => Interlocked.Read(ref _emittedCount);

    /// <summary>Configured emission interval. Exposed so tests can assert config binding.</summary>
    public TimeSpan Interval => _options.ApplicationHeartbeatInterval;

    /// <summary>
    /// Emits one heartbeat to both hubs. Public so tests can drive emission
    /// deterministically without waiting on the timer.
    /// </summary>
    public async Task EmitOnceAsync(CancellationToken ct)
    {
        var payload = new
        {
            timestamp = _timeProvider.GetUtcNow(),
            intervalMs = (long)_options.ApplicationHeartbeatInterval.TotalMilliseconds,
        };

        await _telemetryHub.Clients.All.SendAsync(EventName, payload, ct).ConfigureAwait(false);
        await _streamingHub.Clients.All.SendAsync(EventName, payload, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _emittedCount);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.ApplicationHeartbeatEnabled)
        {
            _logger.LogInformation("Hub application heartbeat disabled by config.");
            return;
        }

        TimeSpan interval = _options.ApplicationHeartbeatInterval;
        if (interval <= TimeSpan.Zero)
        {
            _logger.LogWarning(
                "Hub application heartbeat interval {Interval} is non-positive; disabling emission.",
                interval);
            return;
        }

        _logger.LogInformation(
            "Hub application heartbeat starting at {Interval} interval (keepalive={KeepAlive}, clientTimeout={ClientTimeout}).",
            interval, _options.KeepAliveInterval, _options.ClientTimeoutInterval);

        using var timer = new PeriodicTimer(interval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await EmitOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Log-and-continue: a hub emission failure must not kill the host.
                    // Missing one heartbeat surfaces as a UI stall on the next tick,
                    // which is the desired signal.
                    _logger.LogWarning(ex, "Hub heartbeat emission failed; will retry on the next tick.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
    }
}

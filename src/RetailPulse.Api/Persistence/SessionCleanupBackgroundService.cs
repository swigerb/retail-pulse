using Microsoft.Extensions.Options;

namespace RetailPulse.Api.Persistence;

/// <summary>
/// Periodically deletes sessions and turns whose last activity is older than
/// <see cref="SessionPersistenceOptions.RetentionTtl"/>. Uses <see cref="TimeProvider"/>
/// so tests can drive the clock deterministically instead of sleeping, and logs the
/// per-sweep row counts so retention is observable in production.
///
/// Registered only when <see cref="SessionPersistenceOptions.Enabled"/> is true (feature
/// switch off means no service, no schema, no writes — see
/// <see cref="SessionPersistenceServiceExtensions"/>).
/// </summary>
public sealed class SessionCleanupBackgroundService : BackgroundService
{
    private readonly ISessionStore _store;
    private readonly IOptions<SessionPersistenceOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SessionCleanupBackgroundService> _logger;

    public SessionCleanupBackgroundService(
        ISessionStore store,
        IOptions<SessionPersistenceOptions> options,
        TimeProvider timeProvider,
        ILogger<SessionCleanupBackgroundService> logger)
    {
        _store = store;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SessionPersistenceOptions opts = _options.Value;

        // Zero/negative retention = keep forever; zero/negative interval = don't sweep.
        // Both are documented escape hatches for operators who want the store on but the
        // sweeper off. Failing closed here would be worse than a no-op — you get to keep
        // the durable writes without the deletion cadence.
        if (opts.RetentionTtl <= TimeSpan.Zero || opts.CleanupInterval <= TimeSpan.Zero)
        {
            _logger.LogInformation(
                "Session cleanup disabled (RetentionTtl={Ttl}, CleanupInterval={Interval}).",
                opts.RetentionTtl, opts.CleanupInterval);
            return;
        }

        _logger.LogInformation(
            "Session cleanup started (RetentionTtl={Ttl}, CleanupInterval={Interval}).",
            opts.RetentionTtl, opts.CleanupInterval);

        using PeriodicTimer timer = new(opts.CleanupInterval, _timeProvider);
        try
        {
            do
            {
                try
                {
                    DateTimeOffset cutoff = _timeProvider.GetUtcNow() - opts.RetentionTtl;
                    CleanupResult result = await _store.PurgeExpiredAsync(cutoff, stoppingToken);

                    if (result.SessionsDeleted > 0 || result.TurnsDeleted > 0)
                    {
                        _logger.LogInformation(
                            "Session cleanup swept {Sessions} session(s) and {Turns} turn(s) older than {Cutoff:o}.",
                            result.SessionsDeleted, result.TurnsDeleted, cutoff);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "Session cleanup found nothing older than {Cutoff:o}.", cutoff);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Never let the sweeper crash out — a corrupt row would then be
                    // impossible to evict without a restart. Log loudly and try again
                    // on the next tick.
                    _logger.LogError(ex, "Session cleanup sweep failed; will retry on next interval.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("Session cleanup stopped.");
    }
}

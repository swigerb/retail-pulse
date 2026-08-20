using Microsoft.Extensions.Options;

namespace RetailPulse.Api.Persistence;

/// <summary>
/// Retention sweeper for the durable plan store — the plan-side sibling of
/// <see cref="SessionCleanupBackgroundService"/>. Uses <see cref="TimeProvider"/>
/// so tests can drive the clock deterministically.
/// </summary>
public sealed class PlanCleanupBackgroundService : BackgroundService
{
    private readonly IPlanStore _store;
    private readonly IOptions<PlanPersistenceOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PlanCleanupBackgroundService> _logger;

    public PlanCleanupBackgroundService(
        IPlanStore store,
        IOptions<PlanPersistenceOptions> options,
        TimeProvider timeProvider,
        ILogger<PlanCleanupBackgroundService> logger)
    {
        _store = store;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        PlanPersistenceOptions opts = _options.Value;

        if (opts.RetentionTtl <= TimeSpan.Zero || opts.CleanupInterval <= TimeSpan.Zero)
        {
            _logger.LogInformation(
                "Plan cleanup disabled (RetentionTtl={Ttl}, CleanupInterval={Interval}).",
                opts.RetentionTtl, opts.CleanupInterval);
            return;
        }

        _logger.LogInformation(
            "Plan cleanup started (RetentionTtl={Ttl}, CleanupInterval={Interval}).",
            opts.RetentionTtl, opts.CleanupInterval);

        using PeriodicTimer timer = new(opts.CleanupInterval, _timeProvider);
        try
        {
            do
            {
                try
                {
                    DateTimeOffset cutoff = _timeProvider.GetUtcNow() - opts.RetentionTtl;
                    PlanCleanupResult result = await _store.PurgeExpiredAsync(cutoff, stoppingToken);

                    if (result.PlansDeleted > 0 || result.StepsDeleted > 0)
                    {
                        _logger.LogInformation(
                            "Plan cleanup swept {Plans} plan(s) and {Steps} step(s) older than {Cutoff:o}.",
                            result.PlansDeleted, result.StepsDeleted, cutoff);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Plan cleanup sweep failed; will retry on next interval.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("Plan cleanup stopped.");
    }
}

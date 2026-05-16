using RetailPulse.Api.Middleware;
using RetailPulse.Contracts.Caching;

namespace RetailPulse.Api.Startup;

/// <summary>
/// Background service that validates the response cache infrastructure is operational
/// on application startup. Performs a write/read/delete cycle to confirm connectivity.
/// 
/// The cache is populated organically — the first real request for each query caches
/// the actual AI response (see ChatEndpoints cache-store logic).
/// 
/// Configuration:
///   CacheWarming:Enabled = true/false (default: true in Development)
/// </summary>
public class CacheWarmingService : IHostedService
{
    private readonly IResponseCache _cache;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CacheWarmingService> _logger;

    public CacheWarmingService(
        IResponseCache cache,
        IConfiguration configuration,
        ILogger<CacheWarmingService> logger)
    {
        _cache = cache;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        bool enabled = _configuration.GetValue("CacheWarming:Enabled", true);
        if (!enabled)
        {
            _logger.LogInformation("Cache warming is disabled via configuration");
            return;
        }

        _logger.LogInformation("Validating cache infrastructure...");

        try
        {
            // Perform a write/read/delete cycle to confirm the cache is operational
            const string healthCheckKey = "__cache_health_check__";
            var probe = new CachedResponse(
                Response: "health-check",
                AgentId: "startup-probe",
                CachedAt: DateTime.UtcNow,
                QueryHash: healthCheckKey);

            await _cache.SetAsync(healthCheckKey, probe, TimeSpan.FromSeconds(30), cancellationToken);
            CachedResponse? readBack = await _cache.GetAsync(healthCheckKey, cancellationToken);

            if (readBack is not null)
            {
                _logger.LogInformation("Cache infrastructure validated — ready to serve requests");
            }
            else
            {
                _logger.LogWarning("Cache write succeeded but read-back returned null — cache may be misconfigured");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Cache infrastructure validation failed — responses will not be cached");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

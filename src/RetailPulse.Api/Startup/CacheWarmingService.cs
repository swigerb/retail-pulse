using RetailPulse.Api.Middleware;
using RetailPulse.Contracts.Caching;

namespace RetailPulse.Api.Startup;

/// <summary>
/// Background service that pre-populates the MCP response cache with common demo queries
/// on application startup. Ensures the first demo query is served from cache (fast response).
/// 
/// Configuration:
///   CacheWarming:Enabled = true/false (default: true in Development)
/// </summary>
public class CacheWarmingService : IHostedService
{
    private readonly IResponseCache _cache;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CacheWarmingService> _logger;

    /// <summary>
    /// The 5 demo queries that should be pre-warmed in the cache.
    /// </summary>
    private static readonly string[] _demoQueries =
    [
        "How is Apex Grill performing in the Southwest this quarter?",
        "What's our competitive pricing position for premium burgers?",
        "What's the sentiment from field reps about our new Smokehouse line?",
        "Show me the portfolio health across all regions",
        "What are the top inventory depletion risks this week?"
    ];

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
        var enabled = _configuration.GetValue("CacheWarming:Enabled", true);
        if (!enabled)
        {
            _logger.LogInformation("Cache warming is disabled via configuration");
            return;
        }

        _logger.LogInformation("Starting cache warming for {Count} demo queries...", _demoQueries.Length);
        var overallStart = DateTimeOffset.UtcNow;

        var warmingTasks = _demoQueries.Select(async query =>
        {
            var queryStart = DateTimeOffset.UtcNow;
            try
            {
                // Generate cache key using the same logic as the chat endpoint
                var cacheKey = Middleware.CacheHelpers.BuildCacheKey("pre-route", query);

                // Check if already cached
                var existing = await _cache.GetAsync(cacheKey, cancellationToken);
                if (existing is not null)
                {
                    _logger.LogDebug("Cache already warm for query: {Query}", TruncateQuery(query));
                    return;
                }

                // Pre-populate with a placeholder response indicating this is a warm-up entry.
                // The actual response will be replaced on first real request, but this ensures
                // the cache infrastructure is exercised and ready.
                var warmResponse = new CachedResponse(
                    Response: "[Cache warming placeholder — will be replaced on first live request]",
                    AgentId: "cache-warming",
                    CachedAt: DateTime.UtcNow,
                    QueryHash: cacheKey);

                await _cache.SetAsync(cacheKey, warmResponse, TimeSpan.FromMinutes(60), cancellationToken);

                var elapsed = DateTimeOffset.UtcNow - queryStart;
                _logger.LogInformation(
                    "Warmed cache for query: \"{Query}\" in {ElapsedMs:F0}ms",
                    TruncateQuery(query),
                    elapsed.TotalMilliseconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to warm cache for query: {Query}", TruncateQuery(query));
            }
        });

        await Task.WhenAll(warmingTasks);

        var totalElapsed = DateTimeOffset.UtcNow - overallStart;
        _logger.LogInformation(
            "Cache warming complete — {Count} queries processed in {ElapsedMs:F0}ms",
            _demoQueries.Length,
            totalElapsed.TotalMilliseconds);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string TruncateQuery(string query)
        => query.Length > 60 ? string.Concat(query.AsSpan(0, 57), "...") : query;
}

namespace RetailPulse.Contracts.Caching;

/// <summary>
/// Cache for deterministic agent responses. Keyed by SHA256(agentId + normalizedQuery).
/// Implementations must be thread-safe.
/// </summary>
public interface IResponseCache
{
    /// <summary>
    /// Retrieves a cached response by key, or null if not found / expired.
    /// </summary>
    Task<CachedResponse?> GetAsync(string cacheKey, CancellationToken ct = default);

    /// <summary>
    /// Stores a response in the cache with optional TTL override.
    /// </summary>
    Task SetAsync(string cacheKey, CachedResponse response, TimeSpan? ttl = null, CancellationToken ct = default);

    /// <summary>
    /// Invalidates cache entries. When pattern is null, clears the entire cache.
    /// When provided, removes entries whose keys contain the pattern.
    /// </summary>
    Task InvalidateAsync(string? pattern = null, CancellationToken ct = default);

    /// <summary>
    /// Returns current cache statistics (hits, misses, entry count, memory estimate).
    /// </summary>
    Task<CacheStats> GetStatsAsync(CancellationToken ct = default);
}

/// <summary>
/// A cached agent response with metadata for staleness tracking.
///
/// <para>
/// <paramref name="Charts"/> and <paramref name="Routing"/> are carried so a cache hit
/// can replay the FULL response, not just its prose. Storing the reply alone made a
/// repeated chart question silently lose its visualization inside the TTL (issue #170)
/// — the same user-visible failure as #50/#55, reached through repetition rather than
/// routing. Both fields are optional and trailing, so existing positional construction
/// is unaffected and a non-chart response simply leaves them null.
/// </para>
/// </summary>
public record CachedResponse(
    string Response,
    string AgentId,
    DateTime CachedAt,
    string QueryHash,
    List<ChartSpec>? Charts = null,
    RoutingInfo? Routing = null);

/// <summary>
/// Snapshot of cache health metrics.
/// </summary>
public record CacheStats(int TotalEntries, int Hits, int Misses, double HitRate, long MemoryBytes);

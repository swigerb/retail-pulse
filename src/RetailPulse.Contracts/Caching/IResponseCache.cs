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
/// </summary>
public record CachedResponse(string Response, string AgentId, DateTime CachedAt, string QueryHash);

/// <summary>
/// Snapshot of cache health metrics.
/// </summary>
public record CacheStats(int TotalEntries, int Hits, int Misses, double HitRate, long MemoryBytes);

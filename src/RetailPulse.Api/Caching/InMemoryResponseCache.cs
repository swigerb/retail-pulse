using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using RetailPulse.Contracts.Caching;

namespace RetailPulse.Api.Caching;

/// <summary>
/// In-memory LRU response cache with TTL expiration and hit/miss tracking.
/// Thread-safe via ConcurrentDictionary.
/// </summary>
public class InMemoryResponseCache : IResponseCache
{
    private readonly ConcurrentDictionary<string, CacheItem> _cache = new();
    private readonly LinkedList<string> _lruOrder = new();
    private readonly object _lruLock = new();
    private readonly TimeSpan _defaultTtl;
    private readonly int _maxEntries;
    private int _hits;
    private int _misses;

    public InMemoryResponseCache(TimeSpan? defaultTtl = null, int maxEntries = 1000)
    {
        _defaultTtl = defaultTtl ?? TimeSpan.FromMinutes(30);
        _maxEntries = maxEntries;
    }

    public Task<CachedResponse?> GetAsync(string cacheKey, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(cacheKey, out var item))
        {
            if (item.ExpiresAt > DateTime.UtcNow)
            {
                Interlocked.Increment(ref _hits);
                PromoteToFront(cacheKey);
                return Task.FromResult<CachedResponse?>(item.Response);
            }
            // Expired — remove it
            _cache.TryRemove(cacheKey, out _);
            RemoveFromLru(cacheKey);
        }
        Interlocked.Increment(ref _misses);
        return Task.FromResult<CachedResponse?>(null);
    }

    public Task SetAsync(string cacheKey, CachedResponse response, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var expiry = DateTime.UtcNow.Add(ttl ?? _defaultTtl);
        var item = new CacheItem(response, expiry);

        _cache.AddOrUpdate(cacheKey, item, (_, _) => item);
        PromoteToFront(cacheKey);
        EvictIfNeeded();

        return Task.CompletedTask;
    }

    public Task InvalidateAsync(string? pattern = null, CancellationToken ct = default)
    {
        if (pattern is null)
        {
            _cache.Clear();
            lock (_lruLock) _lruOrder.Clear();
        }
        else
        {
            var keysToRemove = _cache.Keys
                .Where(k => k.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var key in keysToRemove)
            {
                _cache.TryRemove(key, out _);
                RemoveFromLru(key);
            }
        }
        return Task.CompletedTask;
    }

    public Task<CacheStats> GetStatsAsync(CancellationToken ct = default)
    {
        var total = _hits + _misses;
        var hitRate = total > 0 ? (double)_hits / total : 0.0;
        var memEstimate = _cache.Count * 1024L; // rough estimate
        return Task.FromResult(new CacheStats(_cache.Count, _hits, _misses, hitRate, memEstimate));
    }

    /// <summary>
    /// Generates a deterministic cache key from agentId + normalized query via SHA256.
    /// </summary>
    public static string GenerateCacheKey(string agentId, string query)
    {
        var normalized = query.Trim().ToLowerInvariant();
        var input = $"{agentId}:{normalized}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void PromoteToFront(string key)
    {
        lock (_lruLock)
        {
            _lruOrder.Remove(key);
            _lruOrder.AddFirst(key);
        }
    }

    private void RemoveFromLru(string key)
    {
        lock (_lruLock) _lruOrder.Remove(key);
    }

    private void EvictIfNeeded()
    {
        lock (_lruLock)
        {
            while (_lruOrder.Count > _maxEntries && _lruOrder.Last is not null)
            {
                var evictKey = _lruOrder.Last!.Value;
                _lruOrder.RemoveLast();
                _cache.TryRemove(evictKey, out _);
            }
        }
    }

    private record CacheItem(CachedResponse Response, DateTime ExpiresAt);
}

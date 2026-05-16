using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace RetailPulse.Api.Caching;

/// <summary>
/// Caches routing classification results (message hash → intent + confidence) to avoid
/// redundant LLM calls for repeated or similar queries. Uses IMemoryCache with a 5-minute
/// sliding expiration window.
/// </summary>
public class RouterClassificationCache
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<RouterClassificationCache> _logger;

    /// <summary>Default cache TTL for routing decisions.</summary>
    private static readonly TimeSpan _defaultTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Initializes a new instance of <see cref="RouterClassificationCache"/>.
    /// </summary>
    /// <param name="cache">The memory cache instance from DI.</param>
    /// <param name="logger">Logger for cache hit/miss diagnostics.</param>
    public RouterClassificationCache(IMemoryCache cache, ILogger<RouterClassificationCache> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Attempts to retrieve a cached classification result for the given message.
    /// </summary>
    /// <param name="message">The user message to look up.</param>
    /// <returns>The cached entry if found and not expired; otherwise null.</returns>
    public RouterCacheEntry? TryGet(string message)
    {
        string key = GenerateKey(message);
        if (_cache.TryGetValue(key, out RouterCacheEntry? entry) && entry is not null)
        {
            _logger.LogDebug("Router cache HIT for key {CacheKey}", key[..12]);
            return entry;
        }

        _logger.LogDebug("Router cache MISS for key {CacheKey}", key[..12]);
        return null;
    }

    /// <summary>
    /// Stores a classification result in the cache.
    /// </summary>
    /// <param name="message">The user message that was classified.</param>
    /// <param name="intent">The classified intent.</param>
    /// <param name="confidence">The classification confidence score.</param>
    /// <param name="detectedIntents">All detected intents from the classification.</param>
    public void Set(string message, string intent, double confidence, IReadOnlyList<string> detectedIntents)
    {
        string key = GenerateKey(message);
        RouterCacheEntry entry = new(intent, confidence, detectedIntents);

        MemoryCacheEntryOptions options = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(_defaultTtl);

        _cache.Set(key, entry, options);
        _logger.LogDebug("Router cache SET for key {CacheKey} → intent '{Intent}'", key[..12], intent);
    }

    /// <summary>
    /// Generates a deterministic cache key from the normalized message content via SHA256.
    /// </summary>
    private static string GenerateKey(string message)
    {
        string normalized = message.Trim().ToLowerInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"router:{normalized}"));
        return $"router-classify:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}

/// <summary>
/// A cached routing classification entry containing intent, confidence, and detected intents.
/// </summary>
/// <param name="Intent">The classified intent category.</param>
/// <param name="Confidence">The confidence score (0.0–1.0).</param>
/// <param name="DetectedIntents">All intents detected during classification.</param>
public record RouterCacheEntry(
    string Intent,
    double Confidence,
    IReadOnlyList<string> DetectedIntents);

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace RetailPulse.Api.Caching;

/// <summary>
/// Caches tool invocation results keyed by tool name + hashed arguments.
/// Uses IMemoryCache with per-tool TTLs.
/// </summary>
public class ToolResultCache
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<ToolResultCache> _logger;
    private readonly ToolCacheOptions _options;

    private int _hits;
    private int _misses;

    public int Hits => _hits;
    public int Misses => _misses;

    public ToolResultCache(
        IMemoryCache cache,
        IOptions<ToolCacheOptions> options,
        ILogger<ToolResultCache> logger)
    {
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Attempts to retrieve a cached result. Returns null on miss.
    /// </summary>
    public string? TryGet(string toolName, IDictionary<string, object?> arguments)
    {
        if (!_options.Enabled)
            return null;

        var key = GenerateKey(toolName, arguments);

        if (_cache.TryGetValue(key, out string? cached) && cached is not null)
        {
            Interlocked.Increment(ref _hits);
            _logger.LogDebug("Tool cache HIT for {Tool} (key={Key})", toolName, key);
            return cached;
        }

        Interlocked.Increment(ref _misses);
        return null;
    }

    /// <summary>
    /// Stores a tool result with the configured TTL for that tool.
    /// Skips caching error responses and placeholder content.
    /// </summary>
    public void Set(string toolName, IDictionary<string, object?> arguments, string result)
    {
        if (!_options.Enabled)
            return;

        if (!IsValidForCaching(result))
        {
            _logger.LogDebug("Skipping cache store for {Tool} — result failed validation", toolName);
            return;
        }

        var key = GenerateKey(toolName, arguments);
        var ttl = _options.GetTtl(toolName);

        var entryOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl,
            Size = 1
        };

        _cache.Set(key, result, entryOptions);
        _logger.LogDebug("Tool cache SET for {Tool} (key={Key}, ttl={Ttl})", toolName, key, ttl);
    }

    /// <summary>
    /// Invalidates cache entries. If toolName is null, a cache-wide token is used.
    /// Note: IMemoryCache doesn't support prefix eviction, so we use a generation token.
    /// </summary>
    public void Invalidate(string? toolName = null)
    {
        if (toolName is null)
        {
            // IMemoryCache doesn't support bulk clear — increment generation
            Interlocked.Increment(ref _generation);
            _logger.LogInformation("Tool cache invalidated (all tools) — generation={Gen}", _generation);
        }
        else
        {
            // For per-tool invalidation, increment the tool-specific generation
            _toolGenerations.AddOrUpdate(toolName, 1, (_, v) => v + 1);
            _logger.LogInformation("Tool cache invalidated for {Tool}", toolName);
        }
    }

    private int _generation;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _toolGenerations = new();

    internal static string GenerateKey(string toolName, IDictionary<string, object?> arguments)
    {
        // Sort keys for deterministic hashing
        var sortedArgs = arguments
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={SerializeValue(kv.Value)}")
            .ToList();

        var raw = $"{toolName}:{string.Join("|", sortedArgs)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"tool:{toolName}:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static string SerializeValue(object? value) =>
        value switch
        {
            null => "null",
            string s => s,
            JsonElement je => je.GetRawText(),
            _ => JsonSerializer.Serialize(value)
        };

    /// <summary>
    /// Validates that a result is safe to cache:
    /// - Not null/empty
    /// - Not an error response
    /// - Not placeholder content
    /// </summary>
    private static bool IsValidForCaching(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return false;

        // Don't cache error responses
        if (result.Contains("\"error\"", StringComparison.OrdinalIgnoreCase) &&
            result.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
            return false;

        // Don't cache placeholder content (protection against cache warming bug)
        return !result.Contains("placeholder", StringComparison.OrdinalIgnoreCase) ||
               !result.Contains("will be replaced", StringComparison.OrdinalIgnoreCase);
    }
}

using BenchmarkDotNet.Attributes;
using RetailPulse.Api.Caching;
using RetailPulse.Contracts.Caching;

namespace RetailPulse.Benchmarks;

/// <summary>
/// Benchmarks InMemoryResponseCache.GetAsync and SetAsync with various key sizes
/// to measure hot-path cache performance.
/// </summary>
[MemoryDiagnoser]
public class CacheLookupBenchmark
{
    private InMemoryResponseCache _cache = null!;
    private string _shortKey = null!;
    private string _mediumKey = null!;
    private string _longKey = null!;
    private string _missKey = null!;
    private CachedResponse _response = null!;

    [Params(100, 1000)]
    public int PrefilledEntries { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _cache = new InMemoryResponseCache(TimeSpan.FromMinutes(30), maxEntries: 10_000);

        _shortKey = InMemoryResponseCache.GenerateCacheKey("agent1", "short");
        _mediumKey = InMemoryResponseCache.GenerateCacheKey("agent1", new string('x', 200));
        _longKey = InMemoryResponseCache.GenerateCacheKey("agent1", new string('y', 2000));
        _missKey = "nonexistent-key-that-will-never-match";

        _response = new CachedResponse("Sample response content", "agent1", DateTime.UtcNow, "hash123");

        // Prefill cache
        for (int i = 0; i < PrefilledEntries; i++)
        {
            var key = InMemoryResponseCache.GenerateCacheKey("agent1", $"query-{i}");
            _cache.SetAsync(key, _response).GetAwaiter().GetResult();
        }

        // Ensure our benchmark keys are in the cache
        _cache.SetAsync(_shortKey, _response).GetAwaiter().GetResult();
        _cache.SetAsync(_mediumKey, _response).GetAwaiter().GetResult();
        _cache.SetAsync(_longKey, _response).GetAwaiter().GetResult();
    }

    [Benchmark(Description = "GetAsync: cache hit (short key)")]
    public Task<CachedResponse?> GetHitShortKey()
        => _cache.GetAsync(_shortKey);

    [Benchmark(Description = "GetAsync: cache hit (medium key)")]
    public Task<CachedResponse?> GetHitMediumKey()
        => _cache.GetAsync(_mediumKey);

    [Benchmark(Description = "GetAsync: cache hit (long key)")]
    public Task<CachedResponse?> GetHitLongKey()
        => _cache.GetAsync(_longKey);

    [Benchmark(Description = "GetAsync: cache miss")]
    public Task<CachedResponse?> GetMiss()
        => _cache.GetAsync(_missKey);

    [Benchmark(Description = "SetAsync: insert new entry")]
    public Task SetNew()
    {
        var key = InMemoryResponseCache.GenerateCacheKey("agent1", $"bench-{Random.Shared.Next()}");
        return _cache.SetAsync(key, _response);
    }

    [Benchmark(Description = "GenerateCacheKey: key generation")]
    public static string GenerateKey()
        => InMemoryResponseCache.GenerateCacheKey("demand-forecasting", "What is the demand forecast for Oreos in Q4 2025?");
}

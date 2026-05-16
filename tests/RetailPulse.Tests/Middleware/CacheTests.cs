using FluentAssertions;
using RetailPulse.Api.Caching;
using RetailPulse.Contracts.Caching;

namespace RetailPulse.Tests.Middleware;

/// <summary>
/// Tests for InMemoryResponseCache.
/// Covers: Set/Get, TTL expiration, LRU eviction, invalidation,
/// hit/miss stats, thread safety, cache key generation.
/// </summary>
public class CacheTests
{
    private static InMemoryResponseCache CreateCache(
        TimeSpan? ttl = null, int maxEntries = 100)
        => new(ttl ?? TimeSpan.FromMinutes(5), maxEntries);

    private static CachedResponse MakeResponse(
        string reply = "test reply", string agentId = "general")
        => new(reply, agentId, DateTime.UtcNow, "hash-123");

    #region Set + Get

    [Fact]
    public async Task SetThenGet_ReturnsCachedResponse()
    {
        InMemoryResponseCache cache = CreateCache();
        CachedResponse response = MakeResponse("Hello world");
        await cache.SetAsync("key-1", response);

        CachedResponse? result = await cache.GetAsync("key-1");

        result.Should().NotBeNull();
        result.Response.Should().Be("Hello world");
    }

    [Fact]
    public async Task Get_NonexistentKey_ReturnsNull()
    {
        InMemoryResponseCache cache = CreateCache();

        CachedResponse? result = await cache.GetAsync("nonexistent");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Set_OverwritesExistingEntry()
    {
        InMemoryResponseCache cache = CreateCache();
        await cache.SetAsync("key-1", MakeResponse("first"));
        await cache.SetAsync("key-1", MakeResponse("second"));

        CachedResponse? result = await cache.GetAsync("key-1");

        result.Should().NotBeNull();
        result.Response.Should().Be("second");
    }

    #endregion

    #region TTL Expiration

    [Fact]
    public async Task Get_ExpiredEntry_ReturnsNull()
    {
        InMemoryResponseCache cache = CreateCache(ttl: TimeSpan.FromMilliseconds(1));
        await cache.SetAsync("key-1", MakeResponse());

        await Task.Delay(50); // ensure TTL has passed

        CachedResponse? result = await cache.GetAsync("key-1");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Get_NonExpiredEntry_ReturnsValue()
    {
        InMemoryResponseCache cache = CreateCache(ttl: TimeSpan.FromMinutes(10));
        await cache.SetAsync("key-1", MakeResponse("fresh"));

        CachedResponse? result = await cache.GetAsync("key-1");

        result.Should().NotBeNull();
        result.Response.Should().Be("fresh");
    }

    [Fact]
    public async Task Set_CustomTtlOverridesDefault()
    {
        InMemoryResponseCache cache = CreateCache(ttl: TimeSpan.FromMinutes(10));
        // Set with very short TTL override
        await cache.SetAsync("key-1", MakeResponse(), ttl: TimeSpan.FromMilliseconds(1));

        await Task.Delay(50);

        CachedResponse? result = await cache.GetAsync("key-1");
        result.Should().BeNull();
    }

    #endregion

    #region LRU Eviction

    [Fact]
    public async Task LruEviction_WhenMaxEntriesExceeded_RemovesOldest()
    {
        InMemoryResponseCache cache = CreateCache(maxEntries: 3);

        await cache.SetAsync("key-1", MakeResponse("first"));
        await cache.SetAsync("key-2", MakeResponse("second"));
        await cache.SetAsync("key-3", MakeResponse("third"));
        // This should evict key-1 (oldest, least recently used)
        await cache.SetAsync("key-4", MakeResponse("fourth"));

        CachedResponse? evicted = await cache.GetAsync("key-1");
        CachedResponse? retained = await cache.GetAsync("key-4");

        evicted.Should().BeNull();
        retained.Should().NotBeNull();
    }

    [Fact]
    public async Task LruEviction_AccessPromotesEntry()
    {
        InMemoryResponseCache cache = CreateCache(maxEntries: 3);

        await cache.SetAsync("key-1", MakeResponse("first"));
        await cache.SetAsync("key-2", MakeResponse("second"));
        await cache.SetAsync("key-3", MakeResponse("third"));

        // Access key-1 to promote it
        await cache.GetAsync("key-1");

        // key-2 is now the LRU, should be evicted
        await cache.SetAsync("key-4", MakeResponse("fourth"));

        CachedResponse? promoted = await cache.GetAsync("key-1");
        CachedResponse? evicted = await cache.GetAsync("key-2");

        promoted.Should().NotBeNull("key-1 was promoted by access");
        evicted.Should().BeNull("key-2 was LRU and should be evicted");
    }

    #endregion

    #region Invalidation

    [Fact]
    public async Task Invalidate_NullPattern_ClearsAll()
    {
        InMemoryResponseCache cache = CreateCache();
        await cache.SetAsync("key-1", MakeResponse());
        await cache.SetAsync("key-2", MakeResponse());

        await cache.InvalidateAsync(null);

        (await cache.GetAsync("key-1")).Should().BeNull();
        (await cache.GetAsync("key-2")).Should().BeNull();
    }

    [Fact]
    public async Task Invalidate_WithPattern_ClearsMatchingOnly()
    {
        InMemoryResponseCache cache = CreateCache();
        await cache.SetAsync("agent:general:query1", MakeResponse("gen1"));
        await cache.SetAsync("agent:general:query2", MakeResponse("gen2"));
        await cache.SetAsync("agent:demand:query1", MakeResponse("dem1"));

        await cache.InvalidateAsync("general");

        (await cache.GetAsync("agent:general:query1")).Should().BeNull();
        (await cache.GetAsync("agent:general:query2")).Should().BeNull();
        (await cache.GetAsync("agent:demand:query1")).Should().NotBeNull();
    }

    [Fact]
    public async Task Invalidate_NoMatchingPattern_LeavesAllIntact()
    {
        InMemoryResponseCache cache = CreateCache();
        await cache.SetAsync("key-1", MakeResponse());

        await cache.InvalidateAsync("nonexistent");

        (await cache.GetAsync("key-1")).Should().NotBeNull();
    }

    #endregion

    #region Stats

    [Fact]
    public async Task Stats_TracksHitsAccurately()
    {
        InMemoryResponseCache cache = CreateCache();
        await cache.SetAsync("key-1", MakeResponse());

        await cache.GetAsync("key-1"); // hit
        await cache.GetAsync("key-1"); // hit

        CacheStats stats = await cache.GetStatsAsync();
        stats.Hits.Should().Be(2);
    }

    [Fact]
    public async Task Stats_TracksMissesAccurately()
    {
        InMemoryResponseCache cache = CreateCache();

        await cache.GetAsync("missing-1"); // miss
        await cache.GetAsync("missing-2"); // miss
        await cache.GetAsync("missing-3"); // miss

        CacheStats stats = await cache.GetStatsAsync();
        stats.Misses.Should().Be(3);
    }

    [Fact]
    public async Task Stats_HitRate_CalculatedCorrectly()
    {
        InMemoryResponseCache cache = CreateCache();
        await cache.SetAsync("key-1", MakeResponse());

        await cache.GetAsync("key-1");   // hit
        await cache.GetAsync("missing"); // miss

        CacheStats stats = await cache.GetStatsAsync();
        stats.HitRate.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public async Task Stats_EmptyCache_ZeroHitRate()
    {
        InMemoryResponseCache cache = CreateCache();

        CacheStats stats = await cache.GetStatsAsync();
        stats.HitRate.Should().Be(0);
        stats.TotalEntries.Should().Be(0);
    }

    #endregion

    #region Thread Safety

    [Fact]
    public async Task ConcurrentSetGet_DoesNotCorrupt()
    {
        InMemoryResponseCache cache = CreateCache(maxEntries: 500);
        var tasks = new List<Task>();

        for (int i = 0; i < 100; i++)
        {
            string key = $"concurrent-{i}";
            tasks.Add(cache.SetAsync(key, MakeResponse($"value-{i}")));
        }
        await Task.WhenAll(tasks);

        // Verify some entries are readable
        var reads = new List<Task<CachedResponse?>>();
        for (int i = 0; i < 100; i++)
            reads.Add(cache.GetAsync($"concurrent-{i}"));

        CachedResponse?[] results = await Task.WhenAll(reads);
        results.Count(r => r is not null).Should().Be(100);
    }

    [Fact]
    public async Task ConcurrentSetGet_MixedOperations_NoCrash()
    {
        InMemoryResponseCache cache = CreateCache(maxEntries: 50);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tasks = new List<Task>();

        // Writers
        for (int i = 0; i < 50; i++)
        {
            int idx = i;
            tasks.Add(Task.Run(async () => await cache.SetAsync($"key-{idx}", MakeResponse($"val-{idx}")), cts.Token));
        }

        // Readers
        for (int i = 0; i < 50; i++)
        {
            int idx = i;
            tasks.Add(Task.Run(async () =>
            {
                await cache.GetAsync($"key-{idx}");
            }, cts.Token));
        }

        // Should not throw
        await Task.WhenAll(tasks);
    }

    #endregion

    #region Cache Key Generation

    [Fact]
    public void GenerateCacheKey_SameInput_ProducesSameKey()
    {
        string key1 = InMemoryResponseCache.GenerateCacheKey("general", "What is brand X?");
        string key2 = InMemoryResponseCache.GenerateCacheKey("general", "What is brand X?");

        key1.Should().Be(key2);
    }

    [Fact]
    public void GenerateCacheKey_DifferentAgents_SameQuery_DifferentKeys()
    {
        string key1 = InMemoryResponseCache.GenerateCacheKey("general", "What is brand X?");
        string key2 = InMemoryResponseCache.GenerateCacheKey("demand-forecasting", "What is brand X?");

        key1.Should().NotBe(key2);
    }

    [Fact]
    public void GenerateCacheKey_NormalizedCase_ProducesSameKey()
    {
        string key1 = InMemoryResponseCache.GenerateCacheKey("general", "What Is Brand X?");
        string key2 = InMemoryResponseCache.GenerateCacheKey("general", "what is brand x?");

        key1.Should().Be(key2);
    }

    [Fact]
    public void GenerateCacheKey_TrimmedWhitespace_ProducesSameKey()
    {
        string key1 = InMemoryResponseCache.GenerateCacheKey("general", "  What is brand X?  ");
        string key2 = InMemoryResponseCache.GenerateCacheKey("general", "What is brand X?");

        key1.Should().Be(key2);
    }

    [Fact]
    public void GenerateCacheKey_IsSha256HexString()
    {
        string key = InMemoryResponseCache.GenerateCacheKey("general", "test query");

        // SHA256 produces 64 hex characters
        key.Should().HaveLength(64);
        key.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    #endregion
}

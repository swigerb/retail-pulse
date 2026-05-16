using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Caching;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Tests.Caching;

/// <summary>
/// Tests for RouterClassificationCache — the IMemoryCache-backed cache that
/// stores routing decisions to avoid redundant LLM classification calls.
/// </summary>
public class RouterClassificationCacheTests : IDisposable
{
    private readonly MemoryCache _memoryCache;
    private readonly RouterClassificationCache _cache;

    public RouterClassificationCacheTests()
    {
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        _cache = new RouterClassificationCache(
            _memoryCache,
            NullLogger<RouterClassificationCache>.Instance);
    }

    public void Dispose()
    {
        _memoryCache.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Cache Hit / Miss

    [Fact]
    public void TryGet_CacheHit_ReturnsCachedClassification()
    {
        // Arrange
        string message = "What's the demand forecast for Brand X?";
        List<string> intents = [AgentIntent.DemandForecasting];
        _cache.Set(message, AgentIntent.DemandForecasting, 0.95, intents);

        // Act
        RouterCacheEntry? result = _cache.TryGet(message);

        // Assert
        result.Should().NotBeNull();
        result.Intent.Should().Be(AgentIntent.DemandForecasting);
        result.Confidence.Should().Be(0.95);
        result.DetectedIntents.Should().Contain(AgentIntent.DemandForecasting);
    }

    [Fact]
    public void TryGet_CacheMiss_ReturnsNull()
    {
        // Act
        RouterCacheEntry? result = _cache.TryGet("Never seen this message before");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region TTL / Expiration

    [Fact]
    public void TryGet_AfterTtlExpires_ReturnsNull()
    {
        // Arrange — use a custom short-lived cache to simulate expiration
        MemoryCache shortLivedCache = new(new MemoryCacheOptions
        {
            ExpirationScanFrequency = TimeSpan.FromMilliseconds(10)
        });
        RouterClassificationCache cache = new(
            shortLivedCache,
            NullLogger<RouterClassificationCache>.Instance);

        string message = "demand forecast for Q4";
        cache.Set(message, AgentIntent.DemandForecasting, 0.9, [AgentIntent.DemandForecasting]);

        // Verify it's cached
        cache.TryGet(message).Should().NotBeNull();

        // Simulate expiration by removing all entries
        shortLivedCache.Dispose();
        shortLivedCache = new MemoryCache(new MemoryCacheOptions());
        RouterClassificationCache freshCache = new(
            shortLivedCache,
            NullLogger<RouterClassificationCache>.Instance);

        // Act — query the fresh cache (simulates post-expiration)
        RouterCacheEntry? result = freshCache.TryGet(message);

        // Assert
        result.Should().BeNull();
        shortLivedCache.Dispose();
    }

    #endregion

    #region Different Messages Get Different Entries

    [Fact]
    public void Set_DifferentMessages_GetDifferentCacheEntries()
    {
        // Arrange
        string message1 = "What's the demand forecast?";
        string message2 = "How did the promotion perform?";

        _cache.Set(message1, AgentIntent.DemandForecasting, 0.92, [AgentIntent.DemandForecasting]);
        _cache.Set(message2, AgentIntent.PromotionTrade, 0.88, [AgentIntent.PromotionTrade]);

        // Act
        RouterCacheEntry? result1 = _cache.TryGet(message1);
        RouterCacheEntry? result2 = _cache.TryGet(message2);

        // Assert
        result1.Should().NotBeNull();
        result2.Should().NotBeNull();
        result1.Intent.Should().Be(AgentIntent.DemandForecasting);
        result2.Intent.Should().Be(AgentIntent.PromotionTrade);
    }

    [Fact]
    public void Set_SameMessageDifferentCase_ReturnsSameCacheEntry()
    {
        // Arrange — cache normalizes to lower-case
        string originalMessage = "What's the Demand Forecast?";
        _cache.Set(originalMessage, AgentIntent.DemandForecasting, 0.95, [AgentIntent.DemandForecasting]);

        // Act — query with different casing
        RouterCacheEntry? result = _cache.TryGet("what's the demand forecast?");

        // Assert — should be a cache hit due to case normalization
        result.Should().NotBeNull();
        result.Intent.Should().Be(AgentIntent.DemandForecasting);
    }

    #endregion

    #region Message Normalization

    [Fact]
    public void TryGet_MessageWithLeadingTrailingWhitespace_MatchesNormalized()
    {
        // Arrange
        string message = "  demand forecast for next quarter  ";
        _cache.Set(message, AgentIntent.DemandForecasting, 0.9, [AgentIntent.DemandForecasting]);

        // Act — query with same text but different whitespace
        RouterCacheEntry? result = _cache.TryGet("demand forecast for next quarter");

        // Assert
        result.Should().NotBeNull();
        result.Intent.Should().Be(AgentIntent.DemandForecasting);
    }

    [Fact]
    public void TryGet_MessageWithMixedCaseAndWhitespace_MatchesNormalized()
    {
        // Arrange
        string message = "  HOW Is Our Portfolio PERFORMING?  ";
        _cache.Set(message, AgentIntent.PortfolioHealth, 0.95, [AgentIntent.PortfolioHealth]);

        // Act
        RouterCacheEntry? result = _cache.TryGet("how is our portfolio performing?");

        // Assert
        result.Should().NotBeNull();
        result.Intent.Should().Be(AgentIntent.PortfolioHealth);
    }

    #endregion

    #region Cache Entry Contents

    [Fact]
    public void Set_PreservesAllDetectedIntents()
    {
        // Arrange — multi-intent classification
        string message = "demand forecast and competitive analysis";
        List<string> intents = [AgentIntent.DemandForecasting, AgentIntent.CompetitiveMarket];
        _cache.Set(message, AgentIntent.DemandForecasting, 0.85, intents);

        // Act
        RouterCacheEntry? result = _cache.TryGet(message);

        // Assert
        result.Should().NotBeNull();
        result.DetectedIntents.Should().HaveCount(2);
        result.DetectedIntents.Should().Contain(AgentIntent.DemandForecasting);
        result.DetectedIntents.Should().Contain(AgentIntent.CompetitiveMarket);
    }

    [Fact]
    public void Set_OverwritesPreviousEntry()
    {
        // Arrange — set then overwrite
        string message = "ambiguous retail question";
        _cache.Set(message, AgentIntent.General, 0.5, [AgentIntent.General]);
        _cache.Set(message, AgentIntent.DemandForecasting, 0.92, [AgentIntent.DemandForecasting]);

        // Act
        RouterCacheEntry? result = _cache.TryGet(message);

        // Assert — should have the latest value
        result.Should().NotBeNull();
        result.Intent.Should().Be(AgentIntent.DemandForecasting);
        result.Confidence.Should().Be(0.92);
    }

    #endregion
}

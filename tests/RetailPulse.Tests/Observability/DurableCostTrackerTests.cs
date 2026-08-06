using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Observability;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Tests.Observability;

/// <summary>
/// Tests for <see cref="DurableCostTracker"/> — the SQLite-backed cost tracker
/// that replaces the in-memory tracker so cost history survives process restarts
/// and ACA scale-to-zero. Covers durability across restarts, truthful cache-hit
/// semantics (a request is counted but no model tokens/cost), per-model cost
/// attribution, and bounded pruning.
/// </summary>
public sealed class DurableCostTrackerTests : IDisposable
{
    private readonly string _dbPath;

    public DurableCostTrackerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"rp-costs-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        foreach (string f in Directory.EnumerateFiles(
            Path.GetDirectoryName(_dbPath)!, Path.GetFileNameWithoutExtension(_dbPath) + "*"))
        {
            try { File.Delete(f); } catch { /* best effort cleanup */ }
        }
    }

    private static IConfiguration Pricing() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TokenPricing:gpt-5.4-mini:InputPerMillion"] = "0.15",
                ["TokenPricing:gpt-5.4-mini:OutputPerMillion"] = "0.60",
                ["TokenPricing:claude-sonnet:InputPerMillion"] = "3.00",
                ["TokenPricing:claude-sonnet:OutputPerMillion"] = "15.00",
            })
            .Build();

    private DurableCostTracker CreateTracker(ObservabilityOptions? options = null) =>
        new(_dbPath, Options.Create(options ?? new ObservabilityOptions()), Pricing());

    [Fact]
    public async Task History_SurvivesRestart()
    {
        // First "process" writes events, then disposes (simulating scale-to-zero).
        using (DurableCostTracker tracker = CreateTracker())
        {
            await tracker.TrackUsageAsync(new UsageEvent("demand-agent", "gpt-5.4-mini", 1_000, 500, "GetDepletions", DateTime.UtcNow));
            await tracker.TrackUsageAsync(new UsageEvent("promo-agent", "gpt-5.4-mini", 2_000, 1_000, null, DateTime.UtcNow));
        }

        // Second "process" opens the same DB file — history must still be there.
        using DurableCostTracker restarted = CreateTracker();
        CostSummary summary = await restarted.GetSummaryAsync(CostPeriod.All);

        summary.RequestCount.Should().Be(2);
        summary.TotalTokens.Should().Be(4_500);
        summary.TotalCost.Should().BeGreaterThan(0m);
    }

    [Fact]
    public async Task CacheHit_CountsRequest_ButAddsNoTokensOrCost()
    {
        using DurableCostTracker tracker = CreateTracker();

        // A real model call...
        await tracker.TrackUsageAsync(new UsageEvent("demand-agent", "gpt-5.4-mini", 1_000, 500, null, DateTime.UtcNow));
        // ...then a cache hit: zero tokens, flagged, priced at zero.
        await tracker.TrackUsageAsync(new UsageEvent("demand-agent", "cache", 0, 0, null, DateTime.UtcNow, CacheHit: true));

        CostSummary summary = await tracker.GetSummaryAsync(CostPeriod.All);

        summary.RequestCount.Should().Be(2, "the cache hit is a real, observable request");
        summary.TotalTokens.Should().Be(1_500, "cache hits must not fabricate model tokens");

        decimal expected = (1_000m / 1_000_000m * 0.15m) + (500m / 1_000_000m * 0.60m);
        summary.TotalCost.Should().BeApproximately(expected, 0.0000001m);
    }

    [Fact]
    public async Task Cost_IsAttributedPerModel()
    {
        using DurableCostTracker tracker = CreateTracker();
        await tracker.TrackUsageAsync(new UsageEvent("a1", "claude-sonnet", 1_000_000, 1_000_000, null, DateTime.UtcNow));

        CostSummary summary = await tracker.GetSummaryAsync(CostPeriod.All);

        // claude-sonnet: $3/1M input + $15/1M output = $18 for 1M+1M tokens.
        summary.TotalCost.Should().BeApproximately(18.00m, 0.0001m);
    }

    [Fact]
    public async Task ByAgent_GroupsAndOrdersByCost()
    {
        using DurableCostTracker tracker = CreateTracker();
        await tracker.TrackUsageAsync(new UsageEvent("cheap", "gpt-5.4-mini", 100, 100, null, DateTime.UtcNow));
        await tracker.TrackUsageAsync(new UsageEvent("pricey", "claude-sonnet", 100_000, 100_000, null, DateTime.UtcNow));

        IReadOnlyList<AgentCostBreakdown> byAgent = await tracker.GetByAgentAsync(CostPeriod.All);

        byAgent.Should().HaveCount(2);
        byAgent[0].AgentId.Should().Be("pricey", "results are ordered by descending cost");
    }

    [Fact]
    public async Task Prune_EnforcesMaxEventsBound()
    {
        using DurableCostTracker tracker = CreateTracker(new ObservabilityOptions { MaxCostEvents = 5 });

        for (int i = 0; i < 20; i++)
            await tracker.TrackUsageAsync(new UsageEvent($"a{i}", "gpt-5.4-mini", 100, 50, null, DateTime.UtcNow));

        CostSummary summary = await tracker.GetSummaryAsync(CostPeriod.All);
        summary.RequestCount.Should().Be(5, "the durable store is bounded like the in-memory tracker");
    }
}

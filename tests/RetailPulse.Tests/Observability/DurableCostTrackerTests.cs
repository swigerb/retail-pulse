using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Observability;
using RetailPulse.Contracts.Observability;
using RetailPulse.Tests.TestInfrastructure;

namespace RetailPulse.Tests.Observability;

/// <summary>
/// Tests for <see cref="DurableCostTracker"/> — the SQLite-backed cost tracker
/// that replaces the in-memory tracker so cost history survives process restarts.
/// Whether it also survives an ACA replica replacement / scale-to-zero depends on
/// the data directory being a persistent Azure Files mount (see
/// <c>DataDirectoryResolver</c>); these tests exercise the store-level guarantee
/// by reopening the same directory a fresh replica would remount. Also covers
/// truthful cache-hit semantics (a request is counted but no model tokens/cost),
/// per-model cost attribution, and bounded pruning.
/// </summary>
public sealed class DurableCostTrackerTests : IDisposable
{
    private readonly string _dbPath;

    public DurableCostTrackerTests()
    {
        _dbPath = SqliteTestCleanup.NewDbPath("rp-costs");
    }

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_dbPath);

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

    private DurableCostTracker CreateTrackerAt(string dbPath, ObservabilityOptions? options = null) =>
        new(dbPath, Options.Create(options ?? new ObservabilityOptions()), Pricing());

    [Fact]
    public async Task History_SurvivesProcessRestart_OnSameDataDirectory()
    {
        // Proves the store-level guarantee: the DB file persists across process
        // lifetimes. This does NOT prove temp storage survives ACA scale-to-zero —
        // in production the same guarantee only holds because the data directory is
        // an Azure Files mount. Here we reopen the identical path a restarted
        // process would use.
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
    public async Task ReplacementReplica_OnSameMountedDirectory_SeesPriorHistory()
    {
        // Simulate an ACA replica replacement / scale-to-zero: a dedicated
        // directory stands in for the Azure Files mount. One "replica" writes cost
        // events and is fully disposed (the old replica dies); a brand-new tracker
        // instance — a fresh replica remounting the same share at the same path —
        // must observe the prior history. Durability comes from the mounted
        // directory, not the process, which is the whole point of the fix.
        string mountDir = Path.Combine(SqliteTestCleanup.TempRoot, $"rp-mount-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mountDir);
        string mountedDbPath = Path.Combine(mountDir, "costs.db");
        try
        {
            using (DurableCostTracker oldReplica = CreateTrackerAt(mountedDbPath))
            {
                await oldReplica.TrackUsageAsync(new UsageEvent("demand-agent", "claude-sonnet", 1_000_000, 1_000_000, null, DateTime.UtcNow));
            }

            using DurableCostTracker newReplica = CreateTrackerAt(mountedDbPath);
            CostSummary summary = await newReplica.GetSummaryAsync(CostPeriod.All);

            summary.RequestCount.Should().Be(1, "the fresh replica reads the durable store left by the old one");
            summary.TotalCost.Should().BeApproximately(18.00m, 0.0001m);
        }
        finally
        {
            SqliteTestCleanup.ReleaseAndDelete(mountedDbPath);
            try { Directory.Delete(mountDir, recursive: true); } catch { /* best effort */ }
        }
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

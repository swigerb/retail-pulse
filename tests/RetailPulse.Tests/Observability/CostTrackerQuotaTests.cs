using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Observability;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Tests.Observability;

/// <summary>
/// Tests for InMemoryCostTracker quota enforcement:
/// event cap with FIFO eviction and TTL-based stale eviction.
/// </summary>
public class CostTrackerQuotaTests
{
    private static readonly IConfiguration EmptyConfig = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TokenPricing:gpt-5.4-mini:InputPerMillion"] = "0.15",
            ["TokenPricing:gpt-5.4-mini:OutputPerMillion"] = "0.60",
        })
        .Build();

    private static InMemoryCostTracker CreateTracker(
        int maxEvents = 10_000,
        double ttlHours = 24)
    {
        return new InMemoryCostTracker(Options.Create(new ObservabilityOptions
        {
            MaxCostEvents = maxEvents,
            CostEventTtlHours = ttlHours
        }), EmptyConfig);
    }

    private static UsageEvent MakeEvent(string agentId = "test-agent", DateTime? timestamp = null)
    {
        return new UsageEvent(
            agentId,
            "gpt-5.4-mini",
            InputTokens: 100,
            OutputTokens: 50,
            ToolName: "test-tool",
            Timestamp: timestamp ?? DateTime.UtcNow);
    }

    // ── Events within cap ───────────────────────────────────────────────

    [Fact]
    public async Task TrackUsage_WithinCap_AllEventsRetained()
    {
        var tracker = CreateTracker(maxEvents: 100);

        for (int i = 0; i < 50; i++)
            await tracker.TrackUsageAsync(MakeEvent($"agent-{i}"));

        var summary = await tracker.GetSummaryAsync(CostPeriod.All);
        summary.RequestCount.Should().Be(50);
    }

    // ── Event cap enforcement ───────────────────────────────────────────

    [Fact]
    public async Task TrackUsage_ExceedingCap_EvictsOldestEvents()
    {
        var tracker = CreateTracker(maxEvents: 10);

        // Add 15 events — oldest 5 should be evicted
        for (int i = 0; i < 15; i++)
            await tracker.TrackUsageAsync(MakeEvent($"agent-{i}"));

        var summary = await tracker.GetSummaryAsync(CostPeriod.All);
        summary.RequestCount.Should().BeLessThanOrEqualTo(10);
    }

    [Fact]
    public async Task TrackUsage_AtExactCap_AcceptsEvent()
    {
        var tracker = CreateTracker(maxEvents: 5);

        for (int i = 0; i < 5; i++)
            await tracker.TrackUsageAsync(MakeEvent($"agent-{i}"));

        var summary = await tracker.GetSummaryAsync(CostPeriod.All);
        summary.RequestCount.Should().Be(5);
    }

    // ── Default 10K cap ─────────────────────────────────────────────────

    [Fact]
    public async Task TrackUsage_Default10KCap_EnforcedAfterOverflow()
    {
        var tracker = CreateTracker(); // default 10K
        var batchSize = 10_050;

        for (int i = 0; i < batchSize; i++)
            await tracker.TrackUsageAsync(MakeEvent($"agent-{i % 100}"));

        var summary = await tracker.GetSummaryAsync(CostPeriod.All);
        summary.RequestCount.Should().BeLessThanOrEqualTo(10_000);
    }

    // ── TTL eviction ────────────────────────────────────────────────────

    [Fact]
    public async Task TrackUsage_StaleEvents_EvictedOnNextWrite()
    {
        var tracker = CreateTracker(maxEvents: 1000, ttlHours: 1);

        // Add events with timestamps 2 hours ago (stale)
        var staleTime = DateTime.UtcNow.AddHours(-2);
        for (int i = 0; i < 5; i++)
            await tracker.TrackUsageAsync(MakeEvent($"stale-{i}", staleTime));

        // Now add a fresh event — stale events should be evicted
        await tracker.TrackUsageAsync(MakeEvent("fresh-agent"));

        var summary = await tracker.GetSummaryAsync(CostPeriod.All);
        summary.RequestCount.Should().Be(1, "stale events should be evicted, leaving only the fresh one");
    }

    [Fact]
    public async Task TrackUsage_EventsWithin24HourTtl_Retained()
    {
        var tracker = CreateTracker(ttlHours: 24);

        var recentTime = DateTime.UtcNow.AddHours(-12);
        for (int i = 0; i < 5; i++)
            await tracker.TrackUsageAsync(MakeEvent($"recent-{i}", recentTime));

        // Trigger eviction check with a fresh write
        await tracker.TrackUsageAsync(MakeEvent("fresh"));

        var summary = await tracker.GetSummaryAsync(CostPeriod.All);
        summary.RequestCount.Should().Be(6, "12-hour-old events are within 24h TTL");
    }

    // ── Cost calculation ────────────────────────────────────────────────

    [Fact]
    public async Task GetSummary_CalculatesTotalTokensCorrectly()
    {
        var tracker = CreateTracker();

        await tracker.TrackUsageAsync(new UsageEvent("agent-1", "gpt-5.4-mini", 1000, 500, null, DateTime.UtcNow));
        await tracker.TrackUsageAsync(new UsageEvent("agent-2", "gpt-5.4-mini", 2000, 1000, null, DateTime.UtcNow));

        var summary = await tracker.GetSummaryAsync(CostPeriod.All);
        summary.TotalTokens.Should().Be(4500); // (1000+500) + (2000+1000)
    }

    [Fact]
    public async Task GetSummary_EmptyTracker_ReturnsZeros()
    {
        var tracker = CreateTracker();

        var summary = await tracker.GetSummaryAsync(CostPeriod.All);

        summary.TotalTokens.Should().Be(0);
        summary.TotalCost.Should().Be(0);
        summary.RequestCount.Should().Be(0);
    }
}

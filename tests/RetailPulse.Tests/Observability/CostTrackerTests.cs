using FluentAssertions;
using RetailPulse.Api.Observability;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Tests.Observability;

/// <summary>
/// Tests for InMemoryCostTracker — token counting, cost calculation, filtering, trends.
/// </summary>
public class CostTrackerTests
{
    private readonly InMemoryCostTracker _tracker;

    public CostTrackerTests()
    {
        _tracker = new InMemoryCostTracker();
    }

    #region TrackUsageAsync

    [Fact]
    public async Task TrackUsage_IncrementCounters()
    {
        await _tracker.TrackUsageAsync(MakeEvent("agent-1", "gpt-5.4-mini", 100, 50));
        await _tracker.TrackUsageAsync(MakeEvent("agent-1", "gpt-5.4-mini", 200, 100));

        var summary = await _tracker.GetSummaryAsync(CostPeriod.All);

        summary.TotalTokens.Should().Be(450); // 100+50+200+100
        summary.RequestCount.Should().Be(2);
    }

    #endregion

    #region Cost Calculation — Pricing Table

    [Fact]
    public async Task CostCalculation_Gpt54Mini_MatchesPricing()
    {
        // gpt-5.4-mini: $0.15/1M input, $0.60/1M output
        await _tracker.TrackUsageAsync(MakeEvent("agent-1", "gpt-5.4-mini", 1_000_000, 1_000_000));

        var summary = await _tracker.GetSummaryAsync(CostPeriod.All);

        summary.TotalCost.Should().Be(0.15m + 0.60m); // $0.75
    }

    [Fact]
    public async Task CostCalculation_Gpt4o_MatchesPricing()
    {
        // gpt-4o: $2.50/1M input, $10.00/1M output
        await _tracker.TrackUsageAsync(MakeEvent("agent-1", "gpt-4o", 1_000_000, 1_000_000));

        var summary = await _tracker.GetSummaryAsync(CostPeriod.All);

        summary.TotalCost.Should().Be(2.50m + 10.00m); // $12.50
    }

    [Fact]
    public async Task CostCalculation_ClaudeSonnet_MatchesPricing()
    {
        // claude-sonnet: $3.00/1M input, $15.00/1M output
        await _tracker.TrackUsageAsync(MakeEvent("agent-1", "claude-sonnet", 1_000_000, 1_000_000));

        var summary = await _tracker.GetSummaryAsync(CostPeriod.All);

        summary.TotalCost.Should().Be(3.00m + 15.00m); // $18.00
    }

    [Fact]
    public async Task CostCalculation_UnknownModel_UsesDefaultPricing()
    {
        // Unknown model: $1.00/1M input, $5.00/1M output (default)
        await _tracker.TrackUsageAsync(MakeEvent("agent-1", "unknown-model-xyz", 1_000_000, 1_000_000));

        var summary = await _tracker.GetSummaryAsync(CostPeriod.All);

        summary.TotalCost.Should().Be(1.00m + 5.00m); // $6.00 (default)
    }

    [Fact]
    public async Task CostCalculation_SmallTokenCounts_PreciseDecimal()
    {
        // 1000 tokens of gpt-5.4-mini input: 1000/1M * $0.15 = $0.00015
        await _tracker.TrackUsageAsync(MakeEvent("agent-1", "gpt-5.4-mini", 1000, 0));

        var summary = await _tracker.GetSummaryAsync(CostPeriod.All);

        summary.TotalCost.Should().Be(0.00015m);
    }

    #endregion

    #region GetSummaryAsync — Period Filtering

    [Fact]
    public async Task GetSummary_TodayOnly_FiltersCorrectly()
    {
        // Today event
        await _tracker.TrackUsageAsync(MakeEvent("agent-1", "gpt-4o", 500, 500, DateTime.UtcNow));
        // Old event (8 days ago)
        await _tracker.TrackUsageAsync(MakeEvent("agent-2", "gpt-4o", 1000, 1000, DateTime.UtcNow.AddDays(-8)));

        var summary = await _tracker.GetSummaryAsync(CostPeriod.Today);

        summary.RequestCount.Should().Be(1);
        summary.TotalTokens.Should().Be(1000); // 500+500
    }

    [Fact]
    public async Task GetSummary_ThisWeek_FiltersCorrectly()
    {
        await _tracker.TrackUsageAsync(MakeEvent("a1", "gpt-4o", 100, 100, DateTime.UtcNow));
        await _tracker.TrackUsageAsync(MakeEvent("a2", "gpt-4o", 100, 100, DateTime.UtcNow.AddDays(-3)));
        await _tracker.TrackUsageAsync(MakeEvent("a3", "gpt-4o", 100, 100, DateTime.UtcNow.AddDays(-10)));

        var summary = await _tracker.GetSummaryAsync(CostPeriod.Week);

        summary.RequestCount.Should().Be(2); // Today and 3 days ago
    }

    [Fact]
    public async Task GetSummary_ThisMonth_FiltersCorrectly()
    {
        await _tracker.TrackUsageAsync(MakeEvent("a1", "gpt-4o", 100, 100, DateTime.UtcNow));
        await _tracker.TrackUsageAsync(MakeEvent("a2", "gpt-4o", 100, 100, DateTime.UtcNow.AddDays(-15)));
        await _tracker.TrackUsageAsync(MakeEvent("a3", "gpt-4o", 100, 100, DateTime.UtcNow.AddDays(-45)));

        var summary = await _tracker.GetSummaryAsync(CostPeriod.Month);

        summary.RequestCount.Should().Be(2); // Today and 15 days ago
    }

    [Fact]
    public async Task GetSummary_PeriodAll_IncludesEverything()
    {
        await _tracker.TrackUsageAsync(MakeEvent("a1", "gpt-4o", 100, 100, DateTime.UtcNow));
        await _tracker.TrackUsageAsync(MakeEvent("a2", "gpt-4o", 100, 100, DateTime.UtcNow.AddDays(-365)));

        var summary = await _tracker.GetSummaryAsync(CostPeriod.All);

        summary.RequestCount.Should().Be(2);
    }

    #endregion

    #region GetByAgentAsync

    [Fact]
    public async Task GetByAgent_GroupsAndSortsByCostDescending()
    {
        // Agent-1: expensive model
        await _tracker.TrackUsageAsync(MakeEvent("agent-1", "claude-sonnet", 500_000, 500_000));
        // Agent-2: cheap model
        await _tracker.TrackUsageAsync(MakeEvent("agent-2", "gpt-5.4-mini", 100_000, 100_000));

        var agents = await _tracker.GetByAgentAsync(CostPeriod.All);

        agents.Should().HaveCount(2);
        agents[0].AgentId.Should().Be("agent-1"); // claude-sonnet is more expensive
        agents[0].Cost.Should().BeGreaterThan(agents[1].Cost);
    }

    [Fact]
    public async Task GetByAgent_IncludesTopTool()
    {
        await _tracker.TrackUsageAsync(new UsageEvent("agent-1", "gpt-4o", 100, 50, "GetDepletions", DateTime.UtcNow));
        await _tracker.TrackUsageAsync(new UsageEvent("agent-1", "gpt-4o", 100, 50, "GetDepletions", DateTime.UtcNow));
        await _tracker.TrackUsageAsync(new UsageEvent("agent-1", "gpt-4o", 100, 50, "CreateChart", DateTime.UtcNow));

        var agents = await _tracker.GetByAgentAsync(CostPeriod.All);

        agents.Should().HaveCount(1);
        agents[0].TopTool.Should().Be("GetDepletions");
    }

    #endregion

    #region GetTrendAsync

    [Fact]
    public async Task GetTrend_ReturnsDailyAggregates()
    {
        var today = DateTime.UtcNow.Date;
        await _tracker.TrackUsageAsync(MakeEvent("a1", "gpt-4o", 100, 50, today.AddHours(10)));
        await _tracker.TrackUsageAsync(MakeEvent("a1", "gpt-4o", 200, 100, today.AddHours(14)));

        var trend = await _tracker.GetTrendAsync(days: 3);

        trend.Days.Should().HaveCount(3);
        // The last day (today) should have 2 events
        var todayEntry = trend.Days.FirstOrDefault(d => d.Date == today);
        todayEntry.Should().NotBeNull();
        todayEntry!.Tokens.Should().Be(450); // 100+50+200+100
    }

    [Fact]
    public async Task GetTrend_NoDays_EmptyBucketsReturned()
    {
        var trend = await _tracker.GetTrendAsync(days: 5);

        trend.Days.Should().HaveCount(5);
        trend.Days.Should().AllSatisfy(d => d.Cost.Should().Be(0));
    }

    #endregion

    #region Empty Tracker

    [Fact]
    public async Task EmptyTracker_GetSummary_ReturnsZeroValues()
    {
        var summary = await _tracker.GetSummaryAsync(CostPeriod.All);

        summary.TotalTokens.Should().Be(0);
        summary.TotalCost.Should().Be(0);
        summary.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task EmptyTracker_GetByAgent_ReturnsEmptyList()
    {
        var agents = await _tracker.GetByAgentAsync(CostPeriod.All);
        agents.Should().BeEmpty();
    }

    #endregion

    #region Case Insensitive Model Matching

    [Fact]
    public async Task CostCalculation_ModelNameCaseInsensitive()
    {
        await _tracker.TrackUsageAsync(MakeEvent("a1", "GPT-5.4-MINI", 1_000_000, 0));
        await _tracker.TrackUsageAsync(MakeEvent("a2", "gpt-5.4-mini", 1_000_000, 0));

        var summary = await _tracker.GetSummaryAsync(CostPeriod.All);

        // Both should match gpt-5.4-mini pricing ($0.15/1M input)
        summary.TotalCost.Should().Be(0.30m);
    }

    #endregion

    #region Helpers

    private static UsageEvent MakeEvent(string agentId, string model, int input, int output, DateTime? timestamp = null)
        => new(agentId, model, input, output, null, timestamp ?? DateTime.UtcNow);

    #endregion
}

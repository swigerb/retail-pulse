using System.Collections.Concurrent;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Api.Observability;

/// <summary>
/// In-memory cost tracker with model pricing table.
/// Thread-safe via ConcurrentBag for usage events.
/// </summary>
public class InMemoryCostTracker : ICostTracker
{
    private readonly ConcurrentBag<UsageEvent> _events = [];

    // Demo pricing table (per 1M tokens)
    private static readonly Dictionary<string, (decimal InputPer1M, decimal OutputPer1M)> ModelPricing = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-5.4-mini"] = (0.15m, 0.60m),
        ["gpt-4o"] = (2.50m, 10.00m),
        ["claude-sonnet"] = (3.00m, 15.00m),
        // Fallback for unknown models
        ["default"] = (1.00m, 5.00m)
    };

    public Task TrackUsageAsync(UsageEvent usage, CancellationToken ct = default)
    {
        _events.Add(usage);
        return Task.CompletedTask;
    }

    public Task<CostSummary> GetSummaryAsync(CostPeriod period, CancellationToken ct = default)
    {
        var filtered = FilterByPeriod(period);
        var totalTokens = filtered.Sum(e => e.InputTokens + e.OutputTokens);
        var totalCost = filtered.Sum(e => CalculateCost(e));

        return Task.FromResult(new CostSummary(totalTokens, totalCost, filtered.Count, period));
    }

    public Task<IReadOnlyList<AgentCostBreakdown>> GetByAgentAsync(CostPeriod period, CancellationToken ct = default)
    {
        var filtered = FilterByPeriod(period);
        var grouped = filtered
            .GroupBy(e => e.AgentId)
            .Select(g =>
            {
                var tokens = g.Sum(e => e.InputTokens + e.OutputTokens);
                var cost = g.Sum(e => CalculateCost(e));
                var topTool = g
                    .Where(e => e.ToolName != null)
                    .GroupBy(e => e.ToolName!)
                    .OrderByDescending(tg => tg.Count())
                    .Select(tg => tg.Key)
                    .FirstOrDefault() ?? "none";

                return new AgentCostBreakdown(g.Key, tokens, cost, g.Count(), topTool);
            })
            .OrderByDescending(a => a.Cost)
            .ToList();

        return Task.FromResult<IReadOnlyList<AgentCostBreakdown>>(grouped);
    }

    public Task<CostTrend> GetTrendAsync(int days = 7, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var events = _events.Where(e => e.Timestamp >= cutoff).ToList();

        var dailyCosts = Enumerable.Range(0, days)
            .Select(i =>
            {
                var date = DateTime.UtcNow.Date.AddDays(-days + 1 + i);
                var dayEvents = events.Where(e => e.Timestamp.Date == date).ToList();
                var cost = dayEvents.Sum(e => CalculateCost(e));
                var tokens = dayEvents.Sum(e => e.InputTokens + e.OutputTokens);
                return new DailyCost(date, cost, tokens);
            })
            .ToList();

        return Task.FromResult(new CostTrend(dailyCosts));
    }

    private List<UsageEvent> FilterByPeriod(CostPeriod period)
    {
        var now = DateTime.UtcNow;
        var cutoff = period switch
        {
            CostPeriod.Today => now.Date,
            CostPeriod.Week => now.AddDays(-7),
            CostPeriod.Month => now.AddDays(-30),
            CostPeriod.All => DateTime.MinValue,
            _ => DateTime.MinValue
        };

        return _events.Where(e => e.Timestamp >= cutoff).ToList();
    }

    private static decimal CalculateCost(UsageEvent e)
    {
        var pricing = ModelPricing.GetValueOrDefault(e.Model, ModelPricing["default"]);
        var inputCost = (decimal)e.InputTokens / 1_000_000m * pricing.InputPer1M;
        var outputCost = (decimal)e.OutputTokens / 1_000_000m * pricing.OutputPer1M;
        return inputCost + outputCost;
    }
}

using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Api.Observability;

/// <summary>
/// In-memory cost tracker with config-driven model pricing table.
/// Reads pricing from appsettings.json TokenPricing section.
/// Bounded by configurable max events and TTL eviction.
/// Thread-safe via ConcurrentQueue for ordered eviction.
/// </summary>
public class InMemoryCostTracker : ICostTracker
{
    private readonly ConcurrentQueue<UsageEvent> _events = new();
    private int _eventCount;
    private readonly ObservabilityOptions _options;
    private readonly Dictionary<string, (decimal InputPer1M, decimal OutputPer1M)> _modelPricing;

    private static readonly (decimal InputPer1M, decimal OutputPer1M) _defaultPricing = (1.00m, 5.00m);

    public InMemoryCostTracker(IOptions<ObservabilityOptions> options, IConfiguration configuration)
    {
        _options = options.Value;
        _modelPricing = BuildPricingTable(configuration);
    }

    private static Dictionary<string, (decimal InputPer1M, decimal OutputPer1M)> BuildPricingTable(IConfiguration configuration)
    {
        var pricing = new Dictionary<string, (decimal InputPer1M, decimal OutputPer1M)>(StringComparer.OrdinalIgnoreCase);
        var section = configuration.GetSection("TokenPricing");

        foreach (var child in section.GetChildren())
        {
            var inputRate = child.GetValue<decimal>("InputPerMillion");
            var outputRate = child.GetValue<decimal>("OutputPerMillion");
            pricing[child.Key] = (inputRate, outputRate);
        }

        return pricing;
    }

    public Task TrackUsageAsync(UsageEvent usage, CancellationToken ct = default)
    {
        EvictStale();

        // Enforce capacity: drop oldest events when full
        while (Volatile.Read(ref _eventCount) >= _options.MaxCostEvents && _events.TryDequeue(out _))
            Interlocked.Decrement(ref _eventCount);

        _events.Enqueue(usage);
        Interlocked.Increment(ref _eventCount);
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
                    .GroupBy(e => e.ToolName)
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

    /// <summary>Evict events older than the configured TTL.</summary>
    private void EvictStale()
    {
        var cutoff = DateTime.UtcNow.AddHours(-_options.CostEventTtlHours);
        while (_events.TryPeek(out var oldest) && oldest.Timestamp < cutoff)
        {
            if (_events.TryDequeue(out _))
                Interlocked.Decrement(ref _eventCount);
        }
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

        return [.. _events.Where(e => e.Timestamp >= cutoff)];
    }

    private decimal CalculateCost(UsageEvent e)
    {
        var (InputPer1M, OutputPer1M) = _modelPricing.GetValueOrDefault(e.Model, _defaultPricing);
        var inputCost = e.InputTokens / 1_000_000m * InputPer1M;
        var outputCost = e.OutputTokens / 1_000_000m * OutputPer1M;
        return inputCost + outputCost;
    }
}

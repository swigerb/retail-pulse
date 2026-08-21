using System.Collections.Concurrent;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Tests.Rag.FoundryIQ;

/// <summary>Minimal recording <see cref="ICostTracker"/> for cost-attribution tests.</summary>
public sealed class RecordingCostTracker : ICostTracker
{
    public ConcurrentQueue<UsageEvent> Events { get; } = new();

    public Task TrackUsageAsync(UsageEvent usage, CancellationToken ct = default)
    {
        Events.Enqueue(usage);
        return Task.CompletedTask;
    }

    public Task<CostSummary> GetSummaryAsync(CostPeriod period, CancellationToken ct = default) =>
        Task.FromResult(new CostSummary(0, 0m, 0, period));

    public Task<IReadOnlyList<AgentCostBreakdown>> GetByAgentAsync(CostPeriod period, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AgentCostBreakdown>>([]);

    public Task<CostTrend> GetTrendAsync(int days = 7, CancellationToken ct = default) =>
        Task.FromResult(new CostTrend([]));
}

namespace RetailPulse.Contracts.Observability;

/// <summary>
/// Tracks LLM usage costs across agents with model-specific pricing.
/// Thread-safe for concurrent usage event recording.
/// </summary>
public interface ICostTracker
{
    Task TrackUsageAsync(UsageEvent usage, CancellationToken ct = default);
    Task<CostSummary> GetSummaryAsync(CostPeriod period, CancellationToken ct = default);
    Task<IReadOnlyList<AgentCostBreakdown>> GetByAgentAsync(CostPeriod period, CancellationToken ct = default);
    Task<CostTrend> GetTrendAsync(int days = 7, CancellationToken ct = default);
}

public record UsageEvent(string AgentId, string Model, int InputTokens, int OutputTokens, string? ToolName, DateTime Timestamp, bool CacheHit = false);
public record CostSummary(int TotalTokens, decimal TotalCost, int RequestCount, CostPeriod Period);
public record AgentCostBreakdown(string AgentId, int Tokens, decimal Cost, int Requests, string TopTool);
public record CostTrend(IReadOnlyList<DailyCost> Days);
public record DailyCost(DateTime Date, decimal Cost, int Tokens);
public enum CostPeriod { Today, Week, Month, All }

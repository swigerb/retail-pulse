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

/// <summary>
/// One accounted LLM usage event. <see cref="PlanId"/> and <see cref="PlanStepId"/>
/// are additive: they default to <c>null</c> for single-shot turns and are populated
/// only when the usage was produced by a plan-first orchestration step. Persisted
/// cost history therefore attributes each token to both the enclosing plan (roll-up)
/// and the individual step (attribution), without breaking any existing caller.
/// </summary>
public record UsageEvent(
    string AgentId,
    string Model,
    int InputTokens,
    int OutputTokens,
    string? ToolName,
    DateTime Timestamp,
    bool CacheHit = false,
    string? PlanId = null,
    string? PlanStepId = null);

public record CostSummary(int TotalTokens, decimal TotalCost, int RequestCount, CostPeriod Period);
public record AgentCostBreakdown(string AgentId, int Tokens, decimal Cost, int Requests, string TopTool);
public record CostTrend(IReadOnlyList<DailyCost> Days);
public record DailyCost(DateTime Date, decimal Cost, int Tokens);
public enum CostPeriod { Today, Week, Month, All }

using RetailPulse.Api.Observability;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Api.Security.Anonymous;

/// <summary>
/// Decorates the real <see cref="ICostTracker"/> so every recorded usage event is also counted
/// against the anonymous daily <see cref="AnonymousUsageBudget"/>.
///
/// It delegates unchanged to the inner tracker (audit/export/telemetry stay intact) and, in
/// addition, feeds the budget the SAME numbers the cost tracker sees — token totals and the
/// pricing-table cost. Cache hits arrive as zero-token, cache-flagged events, so they add nothing
/// to the token/cost ceilings here, exactly as the pricing table computes zero cost for them. This
/// keeps budget accounting truthful and impossible to inflate or bypass.
/// </summary>
public sealed class AnonymousBudgetCostTracker : ICostTracker
{
    private readonly ICostTracker _inner;
    private readonly AnonymousUsageBudget _budget;
    private readonly TokenPricing _pricing;

    public AnonymousBudgetCostTracker(ICostTracker inner, AnonymousUsageBudget budget, IConfiguration configuration)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _pricing = TokenPricing.FromConfiguration(configuration);
    }

    public async Task TrackUsageAsync(UsageEvent usage, CancellationToken ct = default)
    {
        await _inner.TrackUsageAsync(usage, ct);

        // CacheHit events and zero-token events contribute nothing (pricing returns 0), so a
        // cache hit advances neither the token nor the cost ceiling — only the request ceiling,
        // which is charged up-front at request admission by the guard middleware.
        long tokens = usage.CacheHit ? 0 : usage.InputTokens + usage.OutputTokens;
        decimal cost = _pricing.Calculate(usage);
        _budget.RecordUsage(tokens, cost);
    }

    public Task<CostSummary> GetSummaryAsync(CostPeriod period, CancellationToken ct = default) =>
        _inner.GetSummaryAsync(period, ct);

    public Task<IReadOnlyList<AgentCostBreakdown>> GetByAgentAsync(CostPeriod period, CancellationToken ct = default) =>
        _inner.GetByAgentAsync(period, ct);

    public Task<CostTrend> GetTrendAsync(int days = 7, CancellationToken ct = default) =>
        _inner.GetTrendAsync(days, ct);
}

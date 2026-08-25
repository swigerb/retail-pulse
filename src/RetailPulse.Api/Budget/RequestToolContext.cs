using System.Collections.Concurrent;

namespace RetailPulse.Api.Budget;

/// <summary>
/// Per-request, principal-scoped state for the tool-budget boundary. Backed by
/// <see cref="AsyncLocal{T}"/> so each agent execution gets an isolated context that
/// dies with the request — dedup and cumulative accounting therefore never cross
/// requests or principals by construction.
/// <para>
/// Holds: a dedup map keyed by normalized tool name + arguments (+ principal), a
/// cumulative returned-character counter, a distinct-call counter, and the collected
/// per-tool metrics for telemetry.
/// </para>
/// </summary>
public sealed class RequestToolContext
{
    private static readonly AsyncLocal<RequestToolContext?> _current = new();

    private readonly ConcurrentDictionary<string, string> _dedup = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<ToolResultMetrics> _metrics = new();
    private int _cumulativeChars;
    private int _distinctCalls;

    /// <summary>Stable key for the calling principal (e.g. provider:subject or session id).</summary>
    public string PrincipalKey { get; }

    /// <summary>
    /// True when the current request was classified as an explicit chart-request intent
    /// (see <see cref="Charts.ChartRequestDetector"/>). Budget-boundary callers use this
    /// to apply the tighter <see cref="ToolResultBudgetOptions.MaxToolCallsForChartIntent"/>
    /// cap so ranking/comparison requests can never fan out into per-brand tool storms.
    /// </summary>
    public bool IsChartIntent { get; }

    private RequestToolContext(string principalKey, bool isChartIntent)
    {
        PrincipalKey = principalKey;
        IsChartIntent = isChartIntent;
    }

    public static RequestToolContext? Current => _current.Value;

    /// <summary>
    /// Begin a budget scope for the current async flow. Returns a disposable that clears
    /// the scope on dispose so the AsyncLocal slot does not leak across requests.
    /// </summary>
    public static IDisposable Begin(string principalKey) => Begin(principalKey, isChartIntent: false);

    /// <summary>
    /// Begin a budget scope with an explicit chart-intent flag (see
    /// <see cref="IsChartIntent"/>). Use this overload from the request pipeline when
    /// <see cref="Charts.ChartRequestDetector"/> reports an explicit chart request so
    /// the tighter per-request tool-call cap applies.
    /// </summary>
    public static IDisposable Begin(string principalKey, bool isChartIntent)
    {
        // If a caller already opened an outer budget scope (for example the
        // plan-first orchestrator wrapping a whole plan around per-step
        // specialist invocations — see issue #93 and ADR-014), reuse it so the
        // returned-character counter, dedup map, and distinct-call counter
        // accumulate cumulatively across the whole plan instead of resetting
        // per step. The nested scope is a no-op on dispose in that case so
        // the AsyncLocal slot stays owned by whoever opened it first.
        RequestToolContext? existing = _current.Value;
        if (existing is not null)
            return NestedScope.Instance;

        _current.Value = new RequestToolContext(
            string.IsNullOrWhiteSpace(principalKey) ? "anonymous" : principalKey,
            isChartIntent);
        return new Scope();
    }

    public int CumulativeChars => _cumulativeChars;
    public int DistinctCalls => _distinctCalls;
    public IReadOnlyCollection<ToolResultMetrics> Metrics => [.. _metrics];

    /// <summary>Compose the principal-scoped dedup key for a normalized call.</summary>
    public string BuildKey(string toolName, string normalizedArgs) =>
        $"{PrincipalKey}\u0001{toolName}\u0001{normalizedArgs}";

    public bool TryGetDeduped(string key, out string cachedJson) =>
        _dedup.TryGetValue(key, out cachedJson!);

    /// <summary>Record a fresh (non-deduplicated) result and advance the counters.</summary>
    public void Record(string key, string json, ToolResultMetrics metrics)
    {
        _dedup[key] = json;
        Interlocked.Add(ref _cumulativeChars, metrics.ReturnedChars);
        Interlocked.Increment(ref _distinctCalls);
        _metrics.Enqueue(metrics);
    }

    /// <summary>Record a deduplicated hit (no counter advance for cumulative chars).</summary>
    public void RecordDedup(ToolResultMetrics metrics) => _metrics.Enqueue(metrics);

    private sealed class Scope : IDisposable
    {
        public void Dispose() => _current.Value = null;
    }

    /// <summary>
    /// Disposable returned by <see cref="Begin(string, bool)"/> when an outer
    /// budget scope is already in force. Dispose is intentionally a no-op —
    /// the scope is owned by the caller who opened it first.
    /// </summary>
    private sealed class NestedScope : IDisposable
    {
        public static readonly NestedScope Instance = new();
        public void Dispose() { }
    }
}

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

    private RequestToolContext(string principalKey)
    {
        PrincipalKey = principalKey;
    }

    public static RequestToolContext? Current => _current.Value;

    /// <summary>
    /// Begin a budget scope for the current async flow. Returns a disposable that clears
    /// the scope on dispose so the AsyncLocal slot does not leak across requests.
    /// </summary>
    public static IDisposable Begin(string principalKey)
    {
        _current.Value = new RequestToolContext(string.IsNullOrWhiteSpace(principalKey) ? "anonymous" : principalKey);
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
}

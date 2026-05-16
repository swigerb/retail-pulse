using System.Collections.Concurrent;

namespace RetailPulse.Api.Agents;

/// <summary>
/// Per-request capture for individual tool invocation durations.
/// <para>
/// The auto-invocation pattern (<see cref="Microsoft.Extensions.AI.IChatClient.GetResponseAsync"/>
/// with tools) does not surface per-tool wall-clock time in the returned <c>ChatResponse</c>.
/// To report accurate <c>tool_call</c> span durations we stamp each invocation as it happens
/// inside the tool wrapper and dequeue those stamps in order when assembling spans.
/// </para>
/// <para>
/// Backed by <see cref="AsyncLocal{T}"/> so each request gets its own queue without
/// cross-request interference, even when the SDK fans tool calls onto pool threads.
/// </para>
/// </summary>
internal static class ToolInvocationTimings
{
    private static readonly AsyncLocal<ConcurrentDictionary<string, ConcurrentQueue<long>>?> _current = new();

    /// <summary>
    /// Begin a capture scope for the current async flow. Returns a disposable that
    /// clears the scope on dispose so the AsyncLocal slot doesn't leak across requests.
    /// </summary>
    public static IDisposable Begin()
    {
        _current.Value = new ConcurrentDictionary<string, ConcurrentQueue<long>>(StringComparer.Ordinal);
        return new Scope();
    }

    /// <summary>
    /// Record a tool invocation's wall-clock duration. No-op if no capture scope is active.
    /// </summary>
    public static void Record(string toolName, long durationMs)
    {
        ConcurrentDictionary<string, ConcurrentQueue<long>>? current = _current.Value;
        if (current is null)
            return;

        ConcurrentQueue<long> queue = current.GetOrAdd(toolName, _ => new ConcurrentQueue<long>());
        queue.Enqueue(durationMs);
    }

    /// <summary>
    /// Dequeue the next recorded duration for <paramref name="toolName"/>.
    /// Returns 0 when no capture scope is active or no more recorded values remain for that tool.
    /// </summary>
    public static long TryDequeue(string toolName)
    {
        ConcurrentDictionary<string, ConcurrentQueue<long>>? current = _current.Value;
        return current is null
            ? 0
            : current.TryGetValue(toolName, out ConcurrentQueue<long>? queue)
            && queue.TryDequeue(out long durationMs)
            ? durationMs
            : 0;
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => _current.Value = null;
    }
}

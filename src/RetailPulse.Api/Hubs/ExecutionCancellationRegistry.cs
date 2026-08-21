using System.Collections.Concurrent;

namespace RetailPulse.Api.Hubs;

/// <summary>
/// Bounds the "cancel my in-flight run" surface (issue #92) to the caller
/// that owns the work. The registry is a subject-scoped map from
/// <c>(scope, key)</c> to the <see cref="CancellationTokenSource"/> that drives
/// the request pipeline (fast path) or the plan orchestrator (plan path).
///
/// <para>Ownership is enforced at cancellation time: a caller can cancel only
/// a scope/key it registered itself. An attacker calling <c>POST /api/chat/{id}/cancel</c>
/// with someone else's session id resolves to a 404 the same way the plan and
/// session endpoints resolve cross-subject probes.</para>
///
/// <para>Storage is replica-local and bounded. Registrations return an
/// <see cref="IDisposable"/> that the caller MUST dispose (via
/// <c>using</c>) so a completed request cannot leak a live entry — the
/// registered CTS itself is not disposed by the registry.</para>
/// </summary>
public interface IExecutionCancellationRegistry
{
    /// <summary>Registers a running scope so a matching cancel endpoint can end it.</summary>
    IDisposable Register(string scope, string key, string subject, CancellationTokenSource cts);

    /// <summary>Returns true when <paramref name="subject"/> owns the scope/key and cancellation was triggered.</summary>
    ExecutionCancelResult TryCancel(string scope, string key, string subject);

    /// <summary>Diagnostic: returns the owning subject for a registered scope, or null when absent.</summary>
    string? OwnerOf(string scope, string key);
}

/// <summary>Outcome of <see cref="IExecutionCancellationRegistry.TryCancel"/>.</summary>
public enum ExecutionCancelResult
{
    /// <summary>No registration for the requested scope/key.</summary>
    NotFound,

    /// <summary>Registration exists but is owned by a different subject.</summary>
    Forbidden,

    /// <summary>Cancellation was requested on the caller's own registration.</summary>
    Cancelled,
}

/// <inheritdoc />
public sealed class ExecutionCancellationRegistry : IExecutionCancellationRegistry
{
    /// <summary>Scope key for /api/chat single-shot fast-path runs.</summary>
    public const string ChatScope = "chat";

    /// <summary>Scope key for plan-first orchestrator runs.</summary>
    public const string PlanScope = "plan";

    private const int _maxEntries = 20_000;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public IDisposable Register(string scope, string key, string subject, CancellationTokenSource cts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(cts);

        EvictIfNeeded();

        string composite = Compose(scope, key);
        var entry = new Entry(subject, cts);

        // Last-writer-wins: a concurrent duplicate registration is expected only
        // when the same subject re-issues a request under the same key. Overwrite
        // rather than throwing so the fast path never fails on a benign retry.
        _entries[composite] = entry;

        return new Deregistration(this, composite, entry);
    }

    public ExecutionCancelResult TryCancel(string scope, string key, string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        string composite = Compose(scope, key);
        if (!_entries.TryGetValue(composite, out Entry? entry))
        {
            return ExecutionCancelResult.NotFound;
        }

        if (!string.Equals(entry.Subject, subject, StringComparison.Ordinal))
        {
            return ExecutionCancelResult.Forbidden;
        }

        try
        {
            entry.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Race with request-completion disposing the CTS; treat as not
            // found so the endpoint returns 404 (the run already ended).
            return ExecutionCancelResult.NotFound;
        }

        return ExecutionCancelResult.Cancelled;
    }

    public string? OwnerOf(string scope, string key)
    {
        string composite = Compose(scope, key);
        return _entries.TryGetValue(composite, out Entry? entry) ? entry.Subject : null;
    }

    private static string Compose(string scope, string key) => $"{scope}::{key}";

    private void EvictIfNeeded()
    {
        if (_entries.Count < _maxEntries)
        {
            return;
        }

        // Bounded eviction: drop a batch of arbitrary entries when the cap is
        // reached. A dropped entry only prevents cancellation of that specific
        // in-flight run — the run itself finishes normally.
        foreach (string composite in _entries.Keys.Take(_maxEntries / 10))
        {
            _entries.TryRemove(composite, out _);
        }
    }

    private sealed record Entry(string Subject, CancellationTokenSource Cts);

    private sealed class Deregistration : IDisposable
    {
        private readonly ExecutionCancellationRegistry _owner;
        private readonly string _composite;
        private readonly Entry _entry;
        private int _disposed;

        public Deregistration(ExecutionCancellationRegistry owner, string composite, Entry entry)
        {
            _owner = owner;
            _composite = composite;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            // Only remove when the still-live entry is ours; a later
            // registration under the same composite key must not be dropped
            // by a completing older run.
            _ = ((ICollection<KeyValuePair<string, Entry>>)_owner._entries)
                .Remove(new KeyValuePair<string, Entry>(_composite, _entry));
        }
    }
}

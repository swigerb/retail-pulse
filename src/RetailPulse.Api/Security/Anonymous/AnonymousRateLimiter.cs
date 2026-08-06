using System.Collections.Concurrent;

namespace RetailPulse.Api.Security.Anonymous;

/// <summary>
/// Compact, thread-safe, in-memory fixed-window rate limiter keyed by an arbitrary string
/// (per-subject or per-IP). Used by the anonymous guard for chat limits that need a key the
/// ASP.NET rate limiter partitions cannot easily express (the validated token subject). Windows
/// are one minute; stale buckets are swept opportunistically to bound memory.
///
/// Replica-local like the rest of the anonymous guardrails — acceptable because hosted Anonymous
/// runs at <c>maxReplicas=1</c>.
/// </summary>
public sealed class AnonymousRateLimiter
{
    private sealed class Window
    {
        public long WindowTicks;
        public int Count;
    }

    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _window = TimeSpan.FromMinutes(1);
    private long _lastSweepTicks;

    public AnonymousRateLimiter(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns true when a request under <paramref name="key"/> is within
    /// <paramref name="permitPerMinute"/> for the current one-minute window; false when the limit
    /// is exceeded (fail-closed).
    /// </summary>
    public bool TryAcquire(string key, int permitPerMinute)
    {
        long nowTicks = _timeProvider.GetUtcNow().UtcDateTime.Ticks;
        long windowTicks = nowTicks - (nowTicks % _window.Ticks);

        MaybeSweep(nowTicks);

        Window window = _windows.GetOrAdd(key, _ => new Window { WindowTicks = windowTicks });
        lock (window)
        {
            if (window.WindowTicks != windowTicks)
            {
                window.WindowTicks = windowTicks;
                window.Count = 0;
            }

            if (window.Count >= permitPerMinute)
            {
                return false;
            }

            window.Count++;
            return true;
        }
    }

    private void MaybeSweep(long nowTicks)
    {
        long last = Interlocked.Read(ref _lastSweepTicks);
        if (nowTicks - last < _window.Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastSweepTicks, nowTicks, last) != last)
        {
            return;
        }

        long cutoff = nowTicks - (2 * _window.Ticks);
        foreach (KeyValuePair<string, Window> entry in _windows)
        {
            if (Volatile.Read(ref entry.Value.WindowTicks) < cutoff)
            {
                _windows.TryRemove(entry.Key, out _);
            }
        }
    }
}

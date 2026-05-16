using System.Collections.Concurrent;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Api.Guardrails;

/// <summary>
/// In-memory ring buffer implementation of ISuspiciousRequestLog.
/// Thread-safe. Evicts oldest entries when max capacity is reached.
/// </summary>
public class InMemorySuspiciousRequestLog : ISuspiciousRequestLog
{
    private readonly ConcurrentQueue<SuspiciousRequest> _entries = new();
    private readonly int _maxEntries;
    private int _jailbreakCount;
    private int _piiCount;
    private int _accessDenialCount;
    private readonly DateTime _since = DateTime.UtcNow;

    public InMemorySuspiciousRequestLog(int maxEntries = 100)
    {
        _maxEntries = maxEntries;
    }

    public Task LogAsync(SuspiciousRequest request, CancellationToken ct = default)
    {
        _entries.Enqueue(request);

        // Track stats by type
        switch (request.DetectionType.ToLowerInvariant())
        {
            case "jailbreak":
                Interlocked.Increment(ref _jailbreakCount);
                break;
            case "pii":
                Interlocked.Increment(ref _piiCount);
                break;
            case "access_denial":
                Interlocked.Increment(ref _accessDenialCount);
                break;
            default:
                break;
        }

        // Ring buffer eviction
        while (_entries.Count > _maxEntries)
            _entries.TryDequeue(out _);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SuspiciousRequest>> GetRecentAsync(int count = 50, CancellationToken ct = default)
    {
        var result = _entries
            .Reverse()
            .Take(count)
            .ToList();
        return Task.FromResult<IReadOnlyList<SuspiciousRequest>>(result);
    }

    public Task<GuardrailsStats> GetStatsAsync(CancellationToken ct = default)
    {
        int total = _jailbreakCount + _piiCount + _accessDenialCount;
        return Task.FromResult(new GuardrailsStats(
            TotalBlocked: total,
            JailbreakAttempts: _jailbreakCount,
            PiiDetections: _piiCount,
            AccessDenials: _accessDenialCount,
            Since: _since));
    }
}

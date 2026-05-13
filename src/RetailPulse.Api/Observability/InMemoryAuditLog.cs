using System.Collections.Concurrent;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Api.Observability;

/// <summary>
/// In-memory ring buffer audit log (last 5000 entries).
/// Thread-safe via ConcurrentQueue with overflow trimming.
/// </summary>
public class InMemoryAuditLog : IAuditLog
{
    private readonly ConcurrentQueue<AuditEntry> _entries = new();
    private const int MaxEntries = 5000;

    public Task LogAsync(AuditEntry entry, CancellationToken ct = default)
    {
        _entries.Enqueue(entry);

        // Trim overflow — ring buffer semantics
        while (_entries.Count > MaxEntries)
            _entries.TryDequeue(out _);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken ct = default)
    {
        var results = _entries.AsEnumerable();

        if (query.AgentId is not null)
            results = results.Where(e => e.AgentId.Equals(query.AgentId, StringComparison.OrdinalIgnoreCase));

        if (query.UserId is not null)
            results = results.Where(e => e.UserId.Equals(query.UserId, StringComparison.OrdinalIgnoreCase));

        if (query.From.HasValue)
            results = results.Where(e => e.Timestamp >= query.From.Value);

        if (query.To.HasValue)
            results = results.Where(e => e.Timestamp <= query.To.Value);

        if (query.Action is not null)
            results = results.Where(e => e.Action.Equals(query.Action, StringComparison.OrdinalIgnoreCase));

        var list = results
            .OrderByDescending(e => e.Timestamp)
            .Take(query.Limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<AuditEntry>>(list);
    }

    public Task<AuditStats> GetStatsAsync(CancellationToken ct = default)
    {
        var entries = _entries.ToArray();

        var byAgent = entries
            .GroupBy(e => e.AgentId)
            .ToDictionary(g => g.Key, g => g.Count());

        var byAction = entries
            .GroupBy(e => e.Action)
            .ToDictionary(g => g.Key, g => g.Count());

        return Task.FromResult(new AuditStats(entries.Length, byAgent, byAction));
    }
}

namespace RetailPulse.Contracts.Observability;

/// <summary>
/// Structured audit log for all agent actions — routing, tool calls, approvals.
/// Ring buffer (last 5000 entries) for demo purposes; queryable by agent, user, time range.
/// </summary>
public interface IAuditLog
{
    Task LogAsync(AuditEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken ct = default);
    Task<AuditStats> GetStatsAsync(CancellationToken ct = default);
}

public record AuditEntry(
    string Id, DateTime Timestamp, string UserId, string AgentId,
    string Action, string InputSummary, string OutputSummary,
    int TokensUsed, TimeSpan Duration);

public record AuditQuery(string? AgentId = null, string? UserId = null,
    DateTime? From = null, DateTime? To = null, string? Action = null, int Limit = 50);

public record AuditStats(int TotalActions, Dictionary<string, int> ByAgent, Dictionary<string, int> ByAction);

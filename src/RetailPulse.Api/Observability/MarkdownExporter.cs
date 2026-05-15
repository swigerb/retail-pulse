using System.Globalization;
using System.Text;
using System.Text.Json;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Api.Observability;

/// <summary>
/// Exports conversations as Markdown or JSON.
/// Pulls from the audit log to reconstruct session activity.
/// </summary>
public class MarkdownExporter : IConversationExport
{
    private readonly IAuditLog _auditLog;

    public MarkdownExporter(IAuditLog auditLog)
    {
        _auditLog = auditLog;
    }

    public async Task<ExportResult> ExportAsync(string sessionId, ExportFormat format, CancellationToken ct = default)
    {
        var entries = await _auditLog.QueryAsync(new AuditQuery(Limit: 500), ct);
        var sessionEntries = entries
            .Where(e => e.InputSummary.Contains(sessionId, StringComparison.OrdinalIgnoreCase)
                     || e.Id.StartsWith(sessionId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Timestamp)
            .ToList();

        // If no session-specific entries found, include all recent entries as fallback
        if (sessionEntries.Count == 0)
            sessionEntries = [.. entries.OrderBy(e => e.Timestamp).Take(100)];

        var exportedAt = DateTime.UtcNow;
        string content;
        string fileName;

        if (format == ExportFormat.Markdown)
        {
            content = BuildMarkdown(sessionId, sessionEntries, exportedAt);
            fileName = $"export-{sessionId}-{exportedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.md";
        }
        else
        {
            content = BuildJson(sessionId, sessionEntries, exportedAt);
            fileName = $"export-{sessionId}-{exportedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.json";
        }

        return new ExportResult(content, format, fileName, exportedAt);
    }

    public async Task<IReadOnlyList<ExportableSession>> ListSessionsAsync(CancellationToken ct = default)
    {
        var allEntries = await _auditLog.QueryAsync(new AuditQuery(Limit: 5000), ct);

        // Group by session hints in the audit entries
        var sessions = allEntries
            .GroupBy(e => ExtractSessionHint(e))
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .Select(g => new ExportableSession(
                g.Key,
                g.Min(e => e.Timestamp),
                g.Count(),
                [.. g.Select(e => e.AgentId).Distinct()]))
            .OrderByDescending(s => s.StartedAt)
            .ToList();

        // If no session grouping found, treat all entries as one session
        if (sessions.Count == 0 && allEntries.Count > 0)
        {
            sessions.Add(new ExportableSession(
                "default",
                allEntries.Min(e => e.Timestamp),
                allEntries.Count,
                [.. allEntries.Select(e => e.AgentId).Distinct()]));
        }

        return sessions;
    }

    private static string BuildMarkdown(string sessionId, List<AuditEntry> entries, DateTime exportedAt)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Conversation Export — {sessionId}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Exported:** {exportedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Entries:** {entries.Count}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var entry in entries)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"### [{entry.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] {entry.Action} — {entry.AgentId}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **User:** {entry.UserId}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **Input:** {entry.InputSummary}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **Output:** {entry.OutputSummary}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **Tokens:** {entry.TokensUsed} | **Duration:** {entry.Duration.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)}ms");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildJson(string sessionId, List<AuditEntry> entries, DateTime exportedAt)
    {
        var export = new
        {
            sessionId,
            exportedAt,
            entryCount = entries.Count,
            entries = entries.Select(e => new
            {
                e.Id,
                e.Timestamp,
                e.UserId,
                e.AgentId,
                e.Action,
                e.InputSummary,
                e.OutputSummary,
                e.TokensUsed,
                durationMs = e.Duration.TotalMilliseconds
            })
        };

        return JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string ExtractSessionHint(AuditEntry entry)
    {
        // Try to extract session ID from the entry ID (format: "session-xxx-...")
        if (entry.Id.Contains('-') && entry.Id.Length > 8)
        {
            var parts = entry.Id.Split('-');
            if (parts.Length >= 2)
                return parts[0];
        }
        return "default";
    }
}

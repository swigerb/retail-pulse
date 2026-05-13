namespace RetailPulse.Contracts.Observability;

/// <summary>
/// Exports conversation sessions as Markdown or JSON for compliance and review.
/// </summary>
public interface IConversationExport
{
    Task<ExportResult> ExportAsync(string sessionId, ExportFormat format, CancellationToken ct = default);
    Task<IReadOnlyList<ExportableSession>> ListSessionsAsync(CancellationToken ct = default);
}

public record ExportResult(string Content, ExportFormat Format, string FileName, DateTime ExportedAt);
public record ExportableSession(string SessionId, DateTime StartedAt, int MessageCount, string[] AgentsUsed);
public enum ExportFormat { Markdown, Json }

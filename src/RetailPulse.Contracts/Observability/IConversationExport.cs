namespace RetailPulse.Contracts.Observability;

/// <summary>
/// Exports conversation sessions as Markdown or JSON for compliance and review.
/// </summary>
public interface IConversationExport
{
    Task<ExportResult> ExportAsync(string sessionId, ExportFormat format, CancellationToken ct = default);
    Task<IReadOnlyList<ExportableSession>> ListSessionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns session metadata plus a bounded slice of the conversation for preview,
    /// or <c>null</c> when the session does not exist.
    /// </summary>
    Task<SessionPreview?> GetPreviewAsync(string sessionId, int maxMessages = 20, CancellationToken ct = default);
}

public record ExportResult(string Content, ExportFormat Format, string FileName, DateTime ExportedAt);
public record ExportableSession(string SessionId, DateTime StartedAt, int MessageCount, string[] AgentsUsed, int TotalTokens);

/// <summary>A bounded, previewable slice of a tracked conversation session.</summary>
public record SessionPreview(string SessionId, IReadOnlyList<PreviewMessage> Messages, int TotalMessages);
public record PreviewMessage(string Role, string Content, DateTime Timestamp);

public enum ExportFormat { Markdown, Json }

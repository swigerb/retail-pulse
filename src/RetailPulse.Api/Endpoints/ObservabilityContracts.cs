namespace RetailPulse.Api.Endpoints;

/// <summary>
/// Explicit wire DTOs for the observability HTTP surface. These decouple the
/// stored domain records (which use IDs, TimeSpans, and internal names) from the
/// JSON contract the frontend actually consumes, so property renames on either
/// side can no longer silently break the UI.
/// </summary>
internal static class ObservabilityContracts
{
    /// <summary>Audit row as consumed by the Audit Log viewer. User/Agent are opaque identifiers, not display names.</summary>
    public record AuditEntryDto(
        string Id,
        DateTime Timestamp,
        string UserId,
        string AgentId,
        string Action,
        string InputSummary,
        string OutputSummary,
        int Tokens,
        double DurationMs);

    /// <summary>Exportable session row. <c>StartTime</c> and <c>TotalTokens</c> match the frontend field names.</summary>
    public record ExportSessionDto(
        string SessionId,
        DateTime StartTime,
        int MessageCount,
        string[] AgentsUsed,
        int TotalTokens);

    /// <summary>Bounded preview payload returned by the export preview endpoint.</summary>
    public record ExportPreviewDto(
        string SessionId,
        IReadOnlyList<PreviewMessageDto> Messages,
        int TotalMessages);

    public record PreviewMessageDto(string Role, string Content, DateTime Timestamp);

    /// <summary>Request body for POST export (frontend sends <c>{ "format": "markdown" | "json" }</c>).</summary>
    public record ExportRequest(string? Format);
}

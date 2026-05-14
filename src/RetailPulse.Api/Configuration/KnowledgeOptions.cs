namespace RetailPulse.Api.Configuration;

/// <summary>
/// Quota settings for the in-memory knowledge base.
/// </summary>
public sealed class KnowledgeOptions
{
    public const string SectionName = "Knowledge";

    /// <summary>Max size of a single uploaded document in bytes (default: 10 MB).</summary>
    public long MaxDocumentSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Max total documents stored (default: 100).</summary>
    public int MaxDocuments { get; set; } = 100;

    /// <summary>Max total chunks across all documents (default: 5000).</summary>
    public int MaxChunks { get; set; } = 5_000;
}

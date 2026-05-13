namespace RetailPulse.Contracts.Rag;

/// <summary>
/// Contract for the RAG knowledge base — document ingestion, search, and management.
/// </summary>
public interface IKnowledgeBase
{
    Task<string> IngestDocumentAsync(string title, string content, string source, CancellationToken ct = default);
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK = 5, CancellationToken ct = default);
    Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(CancellationToken ct = default);
    Task DeleteDocumentAsync(string documentId, CancellationToken ct = default);
}

/// <summary>
/// A single search result with relevance scoring and citation metadata.
/// </summary>
public record SearchResult(string DocumentId, string Title, string Chunk, double Score, string Source, int ChunkIndex);

/// <summary>
/// Metadata about an indexed document.
/// </summary>
public record DocumentInfo(string Id, string Title, string Source, DateTime IngestedAt, int ChunkCount);

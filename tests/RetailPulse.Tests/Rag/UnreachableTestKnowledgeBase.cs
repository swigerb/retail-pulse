using RetailPulse.Contracts.Rag;

namespace RetailPulse.Tests.Rag;

/// <summary>
/// Deliberately unreachable <see cref="IKnowledgeBase"/> stub used by the
/// degradation-policy tests. Every data-plane operation throws
/// <see cref="KnowledgeProviderUnavailableException"/> so we can exercise
/// both <see cref="KnowledgeDegradationMode"/> policies without relying on
/// a real cloud backend outage.
///
/// This class also demonstrates the reusable seam future cloud provider
/// issues plug into: implement <see cref="IKnowledgeBase"/>, throw the
/// contract exception when the backend is unreachable, and the degradation
/// decorator handles the rest.
/// </summary>
internal sealed class UnreachableTestKnowledgeBase : IKnowledgeBase
{
    public const string TestProviderName = "UnreachableTestProvider";

    public int ProbeCallCount { get; private set; }
    public int SearchCallCount { get; private set; }
    public int IngestCallCount { get; private set; }
    public int ListCallCount { get; private set; }
    public int DeleteCallCount { get; private set; }

    public KnowledgeBaseCapabilities GetCapabilities() => new(
        ProviderName: TestProviderName,
        Relevance: KnowledgeRelevanceKind.Semantic,
        Persistent: true,
        RequiresCloud: true,
        Quotas: new KnowledgeQuotas(MaxDocuments: 10_000, MaxChunks: 100_000, MaxDocumentSizeBytes: 25 * 1024 * 1024),
        ScoreSemantics: "Test stub; provider is intentionally unreachable.");

    public Task ProbeAsync(CancellationToken ct = default)
    {
        ProbeCallCount++;
        throw new KnowledgeProviderUnavailableException(
            TestProviderName,
            "Unreachable test provider: probe intentionally fails.");
    }

    public Task<string> IngestDocumentAsync(string title, string content, string source, CancellationToken ct = default)
    {
        IngestCallCount++;
        throw new KnowledgeProviderUnavailableException(
            TestProviderName,
            "Unreachable test provider: ingest intentionally fails.");
    }

    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK = 5, CancellationToken ct = default)
    {
        SearchCallCount++;
        throw new KnowledgeProviderUnavailableException(
            TestProviderName,
            "Unreachable test provider: search intentionally fails.");
    }

    public Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        int topK,
        IReadOnlyCollection<string>? sources,
        CancellationToken ct = default)
    {
        SearchCallCount++;
        throw new KnowledgeProviderUnavailableException(
            TestProviderName,
            "Unreachable test provider: scoped search intentionally fails.");
    }

    public Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(CancellationToken ct = default)
    {
        ListCallCount++;
        throw new KnowledgeProviderUnavailableException(
            TestProviderName,
            "Unreachable test provider: list intentionally fails.");
    }

    public Task DeleteDocumentAsync(string documentId, CancellationToken ct = default)
    {
        DeleteCallCount++;
        throw new KnowledgeProviderUnavailableException(
            TestProviderName,
            "Unreachable test provider: delete intentionally fails.");
    }
}

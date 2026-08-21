namespace RetailPulse.Contracts.Rag;

/// <summary>
/// Contract for the RAG knowledge base — document ingestion, search, and management.
///
/// Multiple providers implement this contract (in-memory BM25, optional cloud
/// providers). Providers report their own capabilities via
/// <see cref="GetCapabilities"/> so callers can present accurate relevance
/// semantics (lexical vs semantic) without comparing scores across providers.
/// Scores are provider-local and NOT comparable between implementations.
///
/// A provider that cannot reach its backend (startup or query time) must throw
/// <see cref="KnowledgeProviderUnavailableException"/>. It MUST NOT return an
/// empty result to signal outage — that path is reserved for a genuinely empty
/// corpus. The degradation policy layer decides whether to fail loudly or fall
/// back to the always-available in-memory provider based on configuration.
/// </summary>
public interface IKnowledgeBase
{
    Task<string> IngestDocumentAsync(string title, string content, string source, CancellationToken ct = default);
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK = 5, CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="SearchAsync(string,int,CancellationToken)"/> but
    /// restricted to chunks whose <see cref="SearchResult.Source"/> matches one
    /// of the values in <paramref name="sources"/>. When <paramref name="sources"/>
    /// is <c>null</c> or empty the search is unscoped (identical semantics to
    /// the two-argument overload).
    ///
    /// The filter MUST be applied before top-K / scoring result selection so
    /// callers receive a full <paramref name="topK"/> worth of in-scope hits
    /// rather than a post-filtered remnant of an unscoped result set. Real
    /// providers (in-memory, Azure AI Search) override this with a pre-scoring
    /// filter; the default interface implementation is a post-hoc filter kept
    /// solely so test doubles and legacy stubs stay source-compatible without
    /// a provider-quality filter of their own.
    ///
    /// Source semantics are provider-local but must be identical across
    /// providers: the value tested against <paramref name="sources"/> is the
    /// same string that <see cref="IngestDocumentAsync"/> stored as the
    /// <c>source</c> parameter (typically the document's file name).
    /// </summary>
    async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        int topK,
        IReadOnlyCollection<string>? sources,
        CancellationToken ct = default)
    {
        IReadOnlyList<SearchResult> unscoped = await SearchAsync(query, topK, ct).ConfigureAwait(false);
        if (sources is null || sources.Count == 0)
        {
            return unscoped;
        }

        var allowed = new HashSet<string>(sources, StringComparer.OrdinalIgnoreCase);
        var filtered = new List<SearchResult>(unscoped.Count);
        foreach (SearchResult result in unscoped)
        {
            if (allowed.Contains(result.Source))
            {
                filtered.Add(result);
            }
        }
        return filtered;
    }
    Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(CancellationToken ct = default);
    Task DeleteDocumentAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// Reports honest, static capabilities of this provider so callers can
    /// present accurate relevance semantics and enforce provider-local quotas.
    /// </summary>
    KnowledgeBaseCapabilities GetCapabilities();

    /// <summary>
    /// Verifies the provider is reachable and configured correctly. Called at
    /// startup by the degradation policy layer and may be called opportunistically
    /// by health checks. Throws <see cref="KnowledgeProviderUnavailableException"/>
    /// when the backend is unreachable; other exceptions indicate misconfiguration
    /// or a bug and should propagate.
    /// </summary>
    Task ProbeAsync(CancellationToken ct = default);
}

/// <summary>
/// A single search result with relevance scoring and citation metadata.
/// <see cref="Score"/> is provider-local and MUST NOT be compared across
/// providers — a lexical BM25 score has no relationship to an embedding cosine
/// similarity. Consumers rank within a single provider's result set only.
/// </summary>
public record SearchResult(string DocumentId, string Title, string Chunk, double Score, string Source, int ChunkIndex);

/// <summary>
/// Metadata about an indexed document.
/// </summary>
public record DocumentInfo(string Id, string Title, string Source, DateTime IngestedAt, int ChunkCount);

/// <summary>
/// Describes what kind of relevance a knowledge provider computes. Callers can
/// use this to communicate expected behavior to users without inspecting the
/// underlying implementation.
/// </summary>
public enum KnowledgeRelevanceKind
{
    /// <summary>Keyword / term-frequency matching (e.g. BM25).</summary>
    Lexical = 0,

    /// <summary>Vector / embedding similarity.</summary>
    Semantic = 1,

    /// <summary>Combined lexical + semantic (e.g. hybrid rank fusion).</summary>
    Hybrid = 2,
}

/// <summary>
/// Provider-reported capabilities. All fields are honest, static descriptions
/// of the provider — the abstraction layer never fabricates or normalizes
/// capabilities on the provider's behalf.
/// </summary>
/// <param name="ProviderName">Stable identifier for the provider (e.g. "InMemory").</param>
/// <param name="Relevance">Kind of relevance scoring the provider produces.</param>
/// <param name="Persistent">
/// True when ingested documents survive process restart. False for the volatile
/// in-memory provider.
/// </param>
/// <param name="RequiresCloud">
/// True when the provider depends on a cloud resource (e.g. Azure AI Search)
/// that must be provisioned separately.
/// </param>
/// <param name="Quotas">Provider-enforced quotas on documents / chunks / size.</param>
/// <param name="ScoreSemantics">
/// Human-readable description of the score range and its meaning. Always
/// includes an explicit reminder that scores are not comparable across
/// providers.
/// </param>
/// <param name="SupportsMutation">
/// True when the provider accepts <see cref="IKnowledgeBase.IngestDocumentAsync"/>
/// and <see cref="IKnowledgeBase.DeleteDocumentAsync"/> against its corpus.
/// False for read-only, bring-your-own-corpus providers (Foundry IQ #104)
/// whose corpus lifecycle is owned outside Retail Pulse — those providers
/// throw <see cref="NotSupportedException"/> from the mutation entry points
/// so callers see a first-class capability signal rather than a silent
/// success against a different corpus. Defaults to <c>true</c> so existing
/// mutable providers stay honest without touching their capability record.
/// The shared conformance suite gates its ingest/list/delete assertions on
/// this flag; the read-only path has its own targeted conformance coverage.
/// </param>
public record KnowledgeBaseCapabilities(
    string ProviderName,
    KnowledgeRelevanceKind Relevance,
    bool Persistent,
    bool RequiresCloud,
    KnowledgeQuotas Quotas,
    string ScoreSemantics,
    bool SupportsMutation = true);

/// <summary>
/// Provider-enforced quota limits reported for observability and API surface
/// consistency. Providers that do not enforce a given quota should report the
/// largest value they will accept (never <see cref="int.MaxValue"/> when the
/// backend has a real ceiling).
/// </summary>
public record KnowledgeQuotas(int MaxDocuments, int MaxChunks, long MaxDocumentSizeBytes);

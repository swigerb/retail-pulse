using FluentAssertions;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Tests.Rag;

/// <summary>
/// Shared, provider-agnostic conformance suite for every
/// <see cref="IKnowledgeBase"/> implementation. Encodes contract invariants
/// that are TRULY COMMON across providers — lexical BM25 in-memory today,
/// semantic Azure AI Search / Foundry IQ tomorrow (issues #103 / #104).
///
/// A new provider adds a concrete subclass that returns a fresh, isolated
/// instance from <see cref="CreateProviderAsync"/>. The base class then runs
/// the shared behavioral tests against it.
///
/// Assertions are deliberately loose about ranking specifics: exact score
/// values, cutoffs, and ordering are provider-local and NOT comparable
/// across providers. The suite verifies that ingest works, search finds
/// what was ingested, list and delete round-trip, capabilities are honestly
/// reported, and a healthy provider's <see cref="IKnowledgeBase.ProbeAsync"/>
/// completes without throwing.
/// </summary>
public abstract class KnowledgeBaseConformanceTests
{
    /// <summary>
    /// Creates a fresh, isolated provider instance. Called once per test.
    /// Implementations must ensure state does not leak between tests
    /// (a fresh in-memory instance or a per-test index prefix for cloud).
    /// </summary>
    protected abstract Task<IKnowledgeBase> CreateProviderAsync();

    [Fact]
    public async Task GetCapabilities_ReportsNonEmptyProviderName()
    {
        IKnowledgeBase kb = await CreateProviderAsync();

        KnowledgeBaseCapabilities caps = kb.GetCapabilities();

        caps.ProviderName.Should().NotBeNullOrWhiteSpace(
            "every provider must self-identify so observability endpoints can report it");
        caps.ScoreSemantics.Should().NotBeNullOrWhiteSpace(
            "scores are provider-local and callers must be told so — this string is where we say so");
        caps.Quotas.MaxDocuments.Should().BeGreaterThan(0);
        caps.Quotas.MaxChunks.Should().BeGreaterThan(0);
        caps.Quotas.MaxDocumentSizeBytes.Should().BeGreaterThan(0L);
    }

    [Fact]
    public async Task ProbeAsync_HealthyProvider_Completes()
    {
        IKnowledgeBase kb = await CreateProviderAsync();

        // A healthy provider must not throw. Cloud implementations that
        // cannot reach their backend at test time should throw
        // KnowledgeProviderUnavailableException instead — this test asserts
        // the happy path only.
        Func<Task> probe = () => kb.ProbeAsync();
        await probe.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Search_OnEmptyCorpus_ReturnsEmpty()
    {
        IKnowledgeBase kb = await CreateProviderAsync();

        IReadOnlyList<SearchResult> results = await kb.SearchAsync("anything at all");

        // Empty corpus MUST return an empty list, not throw. A cloud
        // provider that cannot reach its backend must throw
        // KnowledgeProviderUnavailableException — never fake an empty result.
        results.Should().NotBeNull();
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Ingest_ReturnsNonEmptyDocumentId()
    {
        IKnowledgeBase kb = await CreateProviderAsync();

        string id = await kb.IngestDocumentAsync(
            title: "Conformance Doc",
            content: "Retail category management defines the role and metrics for every category.",
            source: "conformance-test");

        id.Should().NotBeNullOrWhiteSpace(
            "every ingested document must have a stable id the caller can use to delete it later");
    }

    [Fact]
    public async Task Ingest_ThenSearch_FindsRelevantContent()
    {
        IKnowledgeBase kb = await CreateProviderAsync();
        await kb.IngestDocumentAsync(
            title: "Holiday Planning",
            content:
                "Holiday displays should go up in early October for maximum impact. " +
                "Themed holiday displays outperform generic seasonal displays. " +
                "Holiday holiday holiday planning is key for retail success.",
            source: "conformance-test");

        IReadOnlyList<SearchResult> results = await kb.SearchAsync("holiday");

        results.Should().NotBeEmpty("ingested content must be discoverable via search");
        results.Should().OnlyContain(r => r.Score >= 0,
            "scores are provider-local but non-negative for every provider");
        results.Should().OnlyContain(r => !string.IsNullOrEmpty(r.DocumentId));
        results.Should().OnlyContain(r => !string.IsNullOrEmpty(r.Title));
        results.Should().OnlyContain(r => r.Chunk != null);
    }

    [Fact]
    public async Task ListDocuments_ReflectsIngestedDocuments()
    {
        IKnowledgeBase kb = await CreateProviderAsync();
        await kb.IngestDocumentAsync("Alpha", "Content about alpha topic in retail.", "src");
        await kb.IngestDocumentAsync("Beta", "Content about beta topic in retail.", "src");

        IReadOnlyList<DocumentInfo> docs = await kb.ListDocumentsAsync();

        docs.Should().HaveCount(2);
        docs.Select(d => d.Title).Should().BeEquivalentTo(["Alpha", "Beta"]);
        docs.Should().OnlyContain(d => !string.IsNullOrEmpty(d.Id));
        docs.Should().OnlyContain(d => d.ChunkCount > 0,
            "an ingested document must have at least one chunk to be searchable");
    }

    [Fact]
    public async Task DeleteDocument_RemovesFromListAndSearch()
    {
        IKnowledgeBase kb = await CreateProviderAsync();
        string aId = await kb.IngestDocumentAsync(
            "Deletable",
            "Uniqueterm-xyzzy content that is only in this document about retail merchandising.",
            "src");
        await kb.IngestDocumentAsync(
            "Keeper",
            "Different content about supply chain resilience with no overlap.",
            "src");

        await kb.DeleteDocumentAsync(aId);

        IReadOnlyList<DocumentInfo> docs = await kb.ListDocumentsAsync();
        docs.Should().HaveCount(1);
        docs.Should().OnlyContain(d => d.Title == "Keeper");

        IReadOnlyList<SearchResult> hits = await kb.SearchAsync("uniqueterm-xyzzy");
        hits.Should().NotContain(r => r.DocumentId == aId,
            "deleted documents must not appear in future searches");
    }

    [Fact]
    public async Task ScopedSearch_RestrictsResultsToRequestedSources()
    {
        // Issue #105: per-agent knowledge binding relies on identical source
        // filtering semantics across providers. Two documents, only one in
        // scope — the in-scope document must be returned and the out-of-scope
        // document must NOT appear even if it shares vocabulary.
        IKnowledgeBase kb = await CreateProviderAsync();
        await kb.IngestDocumentAsync(
            title: "Planogram Reference",
            content: "Uniqueterm-planogram shelf-set anchors keep the top velocity SKUs in the eye-level bay.",
            source: "planogram.md");
        await kb.IngestDocumentAsync(
            title: "Supplier Reference",
            content: "Uniqueterm-supplier distributor service levels for fill rate and backhaul consolidation.",
            source: "supplier.md");

        IReadOnlyList<SearchResult> inScope = await kb.SearchAsync(
            "uniqueterm-planogram uniqueterm-supplier", topK: 5, sources: ["planogram.md"]);

        inScope.Should().NotBeEmpty();
        inScope.Should().OnlyContain(r => r.Source == "planogram.md",
            "scoped search MUST exclude out-of-scope documents from the result set");

        IReadOnlyList<SearchResult> unscoped = await kb.SearchAsync("uniqueterm-planogram uniqueterm-supplier", topK: 5);
        unscoped.Select(r => r.Source).Distinct().Should().Contain("supplier.md",
            "an unscoped search over the same corpus must still surface both sources — " +
            "the scoped result set is the restricted subset, not a permanent filter");
    }

    [Fact]
    public async Task ScopedSearch_EmptySourcesCollection_BehavesLikeUnscoped()
    {
        // Providers must treat null/empty sources as "no filter" so callers
        // can share one code path for the enabled-unscoped and enabled-scoped
        // agent bindings.
        IKnowledgeBase kb = await CreateProviderAsync();
        await kb.IngestDocumentAsync("Doc A", "Uniqueterm-alpha retail merchandising anchor content.", "a.md");
        await kb.IngestDocumentAsync("Doc B", "Uniqueterm-beta retail merchandising execution content.", "b.md");

        IReadOnlyList<SearchResult> unscoped = await kb.SearchAsync("uniqueterm-alpha uniqueterm-beta", topK: 5);
        IReadOnlyList<SearchResult> empty = await kb.SearchAsync("uniqueterm-alpha uniqueterm-beta", topK: 5, sources: []);

        empty.Select(r => r.DocumentId).OrderBy(id => id).Should().BeEquivalentTo(
            unscoped.Select(r => r.DocumentId).OrderBy(id => id),
            "an empty sources collection must be treated as unscoped by every provider");
    }
}

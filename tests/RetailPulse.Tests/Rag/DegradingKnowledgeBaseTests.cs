using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Rag;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Tests.Rag;

/// <summary>
/// Both degradation policies exercised against a deliberately unreachable
/// test provider. Verifies the two hard invariants from the ADR:
///   1. FailLoud propagates the provider's unavailability exception at
///      startup and at query time — the caller sees the failure.
///   2. FallbackToInMemory swaps to the always-available in-memory instance
///      and serves the caller's data plane from it — NEVER an empty result
///      that could be mistaken for an empty corpus.
/// </summary>
public sealed class DegradingKnowledgeBaseTests
{
    private static InMemoryKnowledgeBase CreateInMemoryFallback() => new(
        NullLoggerFactory.Instance.CreateLogger<InMemoryKnowledgeBase>(),
        Options.Create(new KnowledgeOptions()));

    private static DegradingKnowledgeBase Create(
        IKnowledgeBase primary,
        InMemoryKnowledgeBase fallback,
        KnowledgeDegradationMode degradation) =>
        new(primary, fallback, degradation, NullLoggerFactory.Instance.CreateLogger<DegradingKnowledgeBase>());

    // ── FailLoud ──────────────────────────────────────────────────────

    [Fact]
    public async Task FailLoud_StartupProbe_Unreachable_Propagates()
    {
        var primary = new UnreachableTestKnowledgeBase();
        InMemoryKnowledgeBase fallback = CreateInMemoryFallback();
        DegradingKnowledgeBase kb = Create(primary, fallback, KnowledgeDegradationMode.FailLoud);

        Func<Task> probe = () => kb.ProbeAsync();

        await probe.Should().ThrowAsync<KnowledgeProviderUnavailableException>()
            .Where(ex => ex.ProviderName == UnreachableTestKnowledgeBase.TestProviderName);
        kb.PrimaryReplacedByFallback.Should().BeFalse(
            "FailLoud never swaps the primary — startup should just abort");
    }

    [Fact]
    public async Task FailLoud_Search_PropagatesProviderException()
    {
        var primary = new UnreachableTestKnowledgeBase();
        InMemoryKnowledgeBase fallback = CreateInMemoryFallback();
        await fallback.IngestDocumentAsync("Retail Playbook", "Retail category management guidance.", "src");
        DegradingKnowledgeBase kb = Create(primary, fallback, KnowledgeDegradationMode.FailLoud);

        Func<Task> search = () => kb.SearchAsync("retail");

        await search.Should().ThrowAsync<KnowledgeProviderUnavailableException>();
        primary.SearchCallCount.Should().Be(1);
        // The endpoint layer converts this to a 5xx — NEVER an empty result.
    }

    [Fact]
    public async Task FailLoud_Ingest_PropagatesProviderException()
    {
        var primary = new UnreachableTestKnowledgeBase();
        DegradingKnowledgeBase kb = Create(primary, CreateInMemoryFallback(), KnowledgeDegradationMode.FailLoud);

        Func<Task> ingest = () => kb.IngestDocumentAsync("Doc", "Content about retail.", "src");

        await ingest.Should().ThrowAsync<KnowledgeProviderUnavailableException>();
    }

    // ── FallbackToInMemory ────────────────────────────────────────────

    [Fact]
    public async Task FallbackToInMemory_StartupProbe_Unreachable_SwapsToFallback()
    {
        var primary = new UnreachableTestKnowledgeBase();
        InMemoryKnowledgeBase fallback = CreateInMemoryFallback();
        DegradingKnowledgeBase kb = Create(primary, fallback, KnowledgeDegradationMode.FallbackToInMemory);

        await kb.ProbeAsync();

        kb.PrimaryReplacedByFallback.Should().BeTrue();
        kb.ActiveProviderName.Should().Be(InMemoryKnowledgeBase.ProviderName);
    }

    [Fact]
    public async Task FallbackToInMemory_SwappedAtStartup_SearchServedFromInMemory()
    {
        var primary = new UnreachableTestKnowledgeBase();
        InMemoryKnowledgeBase fallback = CreateInMemoryFallback();
        await fallback.IngestDocumentAsync("Fallback Doc",
            "Holiday inventory management builds up starting eight weeks before peak season. " +
            "Holiday planning drives holiday displays and holiday holiday holiday results.",
            "fallback-src");
        DegradingKnowledgeBase kb = Create(primary, fallback, KnowledgeDegradationMode.FallbackToInMemory);

        await kb.ProbeAsync();
        IReadOnlyList<SearchResult> results = await kb.SearchAsync("holiday");

        // Search hit the fallback, not the unreachable primary — and returned
        // a real result, not an empty list.
        results.Should().NotBeEmpty(
            "after fallback the caller sees the in-memory corpus — never an empty result masquerading as no matches");
        primary.SearchCallCount.Should().Be(0,
            "once swapped at startup, the primary is not touched by subsequent search calls");
    }

    [Fact]
    public async Task FallbackToInMemory_ProbeSucceeded_SearchFailsAtQueryTime_UsesFallback()
    {
        // Simulates a provider that was healthy at startup but transient-fails
        // at query time. The wrapper serves this one call from the fallback
        // while leaving the primary as the configured provider going forward.
        var primary = new FlakyTestKnowledgeBase();
        InMemoryKnowledgeBase fallback = CreateInMemoryFallback();
        await fallback.IngestDocumentAsync("Backup Doc",
            "Category management playbook covers destination categories and routine categories. " +
            "Category category category category category management in retail is a common playbook topic.",
            "fallback");
        DegradingKnowledgeBase kb = Create(primary, fallback, KnowledgeDegradationMode.FallbackToInMemory);

        await kb.ProbeAsync();
        kb.PrimaryReplacedByFallback.Should().BeFalse(
            "the healthy probe means the primary is still active going forward");

        primary.FailNextSearch();
        IReadOnlyList<SearchResult> results = await kb.SearchAsync("category");

        results.Should().NotBeEmpty(
            "the query-time failure must be served from the fallback, not returned as empty");
        primary.SearchCallCount.Should().Be(1,
            "the wrapper tried the primary once and, on the outage exception, delegated to the fallback");
    }

    [Fact]
    public async Task FallbackToInMemory_NonAvailabilityException_StillPropagates()
    {
        // A misconfiguration bug (e.g. bad argument) must NOT be silently
        // swallowed by the fallback — only the outage exception is caught.
        var primary = new ThrowingTestKnowledgeBase(new InvalidOperationException("bad config"));
        DegradingKnowledgeBase kb = Create(primary, CreateInMemoryFallback(), KnowledgeDegradationMode.FallbackToInMemory);

        Func<Task> search = () => kb.SearchAsync("anything");

        await search.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Support stubs ─────────────────────────────────────────────────

    private sealed class FlakyTestKnowledgeBase : IKnowledgeBase
    {
        public int SearchCallCount { get; private set; }
        private bool _failNext;

        public void FailNextSearch() => _failNext = true;

        public KnowledgeBaseCapabilities GetCapabilities() => new(
            ProviderName: "FlakyTestProvider",
            Relevance: KnowledgeRelevanceKind.Semantic,
            Persistent: true,
            RequiresCloud: true,
            Quotas: new KnowledgeQuotas(1, 1, 1),
            ScoreSemantics: "Test stub.");

        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> IngestDocumentAsync(string title, string content, string source, CancellationToken ct = default) =>
            Task.FromResult(Guid.NewGuid().ToString("N"));

        public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK = 5, CancellationToken ct = default)
        {
            SearchCallCount++;
            if (_failNext)
            {
                _failNext = false;
                throw new KnowledgeProviderUnavailableException("FlakyTestProvider", "Simulated query-time outage.");
            }
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);
        }

        public Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DocumentInfo>>([]);

        public Task DeleteDocumentAsync(string documentId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ThrowingTestKnowledgeBase : IKnowledgeBase
    {
        private readonly Exception _exception;

        public ThrowingTestKnowledgeBase(Exception exception) { _exception = exception; }

        public KnowledgeBaseCapabilities GetCapabilities() => new(
            ProviderName: "ThrowingTestProvider",
            Relevance: KnowledgeRelevanceKind.Semantic,
            Persistent: true,
            RequiresCloud: true,
            Quotas: new KnowledgeQuotas(1, 1, 1),
            ScoreSemantics: "Test stub.");

        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> IngestDocumentAsync(string title, string content, string source, CancellationToken ct = default) => throw _exception;
        public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK = 5, CancellationToken ct = default) => throw _exception;
        public Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(CancellationToken ct = default) => throw _exception;
        public Task DeleteDocumentAsync(string documentId, CancellationToken ct = default) => throw _exception;
    }
}

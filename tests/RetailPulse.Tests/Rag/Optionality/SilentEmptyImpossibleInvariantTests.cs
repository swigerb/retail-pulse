using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Rag;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Tests.Rag.Optionality;

/// <summary>
/// Property-style invariant for issue #107: the platform NEVER returns a
/// silent empty result set from <see cref="DegradingKnowledgeBase.SearchAsync(string, int, System.Threading.CancellationToken)"/>
/// as a consequence of a provider outage - because a silent empty would
/// produce a confident ungrounded answer.
///
/// The invariant is checked over the full state space of the decorator:
///
/// <list type="bullet">
///   <item><b>FailLoud</b> - both before and after a failed startup probe, a
///     query-time <see cref="KnowledgeProviderUnavailableException"/> MUST
///     propagate. The exception is the ONLY signal callers see; there is no
///     empty-result path.</item>
///   <item><b>FallbackToInMemory</b> - either the startup probe swapped the
///     active provider to the in-memory fallback (subsequent searches use it),
///     or the runtime handler retries against the fallback. In both cases the
///     caller receives a genuine, honest response from the fallback - never
///     an empty result that leaks the outage.</item>
///   <item>An empty result from a search is legitimate only when the ACTIVE
///     provider returned an empty list itself (the corpus really has nothing
///     for the query). The decorator MUST NOT synthesize an empty list.</item>
/// </list>
///
/// The tests are deterministic; the state space is finite so we enumerate it
/// exhaustively rather than run random property tests.
/// </summary>
public sealed class SilentEmptyImpossibleInvariantTests
{
    private static InMemoryKnowledgeBase CreateInMemoryFallback() => new(
        NullLoggerFactory.Instance.CreateLogger<InMemoryKnowledgeBase>(),
        Options.Create(new KnowledgeOptions()));

    private static DegradingKnowledgeBase Create(
        IKnowledgeBase primary,
        InMemoryKnowledgeBase fallback,
        KnowledgeDegradationMode mode) =>
        new(primary, fallback, mode,
            NullLoggerFactory.Instance.CreateLogger<DegradingKnowledgeBase>());

    [Fact]
    public async Task Invariant_FailLoud_Search_NeverReturnsSilentEmpty_WhenPrimaryUnavailable()
    {
        var primary = new UnreachableTestKnowledgeBase();
        DegradingKnowledgeBase kb = Create(primary, CreateInMemoryFallback(), KnowledgeDegradationMode.FailLoud);

        // Startup probe throws — assert that.
        await FluentActions
            .Awaiting(() => kb.ProbeAsync())
            .Should().ThrowAsync<KnowledgeProviderUnavailableException>();

        // Post-startup queries continue to throw (no silent empty).
        await FluentActions
            .Awaiting(() => kb.SearchAsync("anything"))
            .Should().ThrowAsync<KnowledgeProviderUnavailableException>();

        // Scoped queries do too.
        await FluentActions
            .Awaiting(() => kb.SearchAsync("anything", topK: 5, sources: ["x"]))
            .Should().ThrowAsync<KnowledgeProviderUnavailableException>();
    }

    [Fact]
    public async Task Invariant_FallbackToInMemory_ProbeFailure_SwapsAndReturnsHonestResult()
    {
        var primary = new UnreachableTestKnowledgeBase();
        InMemoryKnowledgeBase fallback = CreateInMemoryFallback();
        await fallback.IngestDocumentAsync(
            "Fallback Doc",
            "Retail category management defines the metrics for every category.",
            "fallback-src");

        DegradingKnowledgeBase kb = Create(primary, fallback, KnowledgeDegradationMode.FallbackToInMemory);
        await kb.ProbeAsync();  // swaps to fallback
        kb.PrimaryReplacedByFallback.Should().BeTrue();

        IReadOnlyList<SearchResult> results = await kb.SearchAsync("category management");

        // The response reflects the fallback's actual corpus. NOT an empty list.
        results.Should().NotBeEmpty(
            "the fallback had matching content; an empty list here would masquerade the outage as a genuine empty corpus");
        results.Should().OnlyContain(r => r.Source == "fallback-src");
    }

    [Fact]
    public async Task Invariant_FallbackToInMemory_RuntimeFailure_RetriesOnFallback()
    {
        // Primary passes probe (a healthy stub) but then fails on the query.
        // The decorator must retry against the in-memory fallback for that
        // single request and return the fallback's genuine result.
        var primary = new HealthyProbeButUnavailableSearchStub();
        InMemoryKnowledgeBase fallback = CreateInMemoryFallback();
        await fallback.IngestDocumentAsync(
            "Runtime Fallback Doc",
            "Retail merchandising execution rewards disciplined planogram compliance.",
            "runtime-src");

        DegradingKnowledgeBase kb = Create(primary, fallback, KnowledgeDegradationMode.FallbackToInMemory);
        await kb.ProbeAsync();  // primary probe ok - no swap
        kb.PrimaryReplacedByFallback.Should().BeFalse();

        IReadOnlyList<SearchResult> results = await kb.SearchAsync("merchandising execution planogram");

        results.Should().NotBeEmpty(
            "runtime fallback must return the fallback corpus's genuine hits, never an empty list");
        results.Should().OnlyContain(r => r.Source == "runtime-src");
    }

    [Fact]
    public async Task Invariant_LegitimateEmpty_FromHealthyProvider_IsPassedThrough()
    {
        // An empty result from a healthy provider IS legitimate - the corpus
        // really has nothing for the query. The decorator must pass it through
        // unchanged (not attempt a fallback and not conflate it with an outage).
        var primary = CreateInMemoryFallback();
        var fallback = CreateInMemoryFallback();
        await fallback.IngestDocumentAsync(
            "Never-Reached Doc",
            "Content only reachable via the fallback bucket.",
            "fallback-only");

        DegradingKnowledgeBase kb = Create(primary, fallback, KnowledgeDegradationMode.FallbackToInMemory);
        await kb.ProbeAsync();

        IReadOnlyList<SearchResult> results = await kb.SearchAsync("nothing in the primary corpus matches this");

        results.Should().BeEmpty(
            "an empty corpus response from a healthy primary is a genuine empty; the decorator must NOT fall back");
    }

    /// <summary>
    /// Passes <see cref="IKnowledgeBase.ProbeAsync(System.Threading.CancellationToken)"/>
    /// but throws <see cref="KnowledgeProviderUnavailableException"/> on the
    /// data plane. Used to exercise the query-time fallback branch that isn't
    /// reachable from <see cref="UnreachableTestKnowledgeBase"/> (whose probe
    /// throws before startup completes).
    /// </summary>
    private sealed class HealthyProbeButUnavailableSearchStub : IKnowledgeBase
    {
        private const string Name = "HealthyProbeUnavailableSearch";

        public KnowledgeBaseCapabilities GetCapabilities() => new(
            Name, KnowledgeRelevanceKind.Semantic, Persistent: true, RequiresCloud: true,
            new KnowledgeQuotas(1_000, 10_000, 25 * 1024 * 1024),
            "Test stub; runtime unavailable.");

        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> IngestDocumentAsync(string title, string content, string source, CancellationToken ct = default) =>
            throw new KnowledgeProviderUnavailableException(Name, "unavailable at query time");

        public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK = 5, CancellationToken ct = default) =>
            throw new KnowledgeProviderUnavailableException(Name, "unavailable at query time");

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            string query, int topK, IReadOnlyCollection<string>? sources, CancellationToken ct = default) =>
            throw new KnowledgeProviderUnavailableException(Name, "unavailable at query time");

        public Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(CancellationToken ct = default) =>
            throw new KnowledgeProviderUnavailableException(Name, "unavailable at query time");

        public Task DeleteDocumentAsync(string documentId, CancellationToken ct = default) =>
            throw new KnowledgeProviderUnavailableException(Name, "unavailable at query time");
    }
}

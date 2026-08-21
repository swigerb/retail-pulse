using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Models;
using RetailPulse.Api.Rag;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Tests.Rag.Parity;

/// <summary>
/// Per-agent knowledge binding (issue #105) must behave identically across
/// providers - the agent's declared source scope propagates through the
/// <see cref="RagContextProvider"/> to the provider's scoped
/// <see cref="IKnowledgeBase.SearchAsync(string, int, IReadOnlyCollection{string}, CancellationToken)"/>
/// overload regardless of provider choice.
///
/// The shared <see cref="KnowledgeBaseConformanceTests.ScopedSearch_RestrictsResultsToRequestedSources"/>
/// suite proves the raw provider contract. This suite proves the pipeline
/// glue - registry binding + <see cref="RagContextProvider"/> forwarding -
/// carries the scope from the agent map to the provider's scoped overload
/// on every provider that supports mutation.
///
/// Foundry IQ's read-only mutation shape means we can't seed a corpus in
/// process; the Foundry parity assertion for scoped retrieval lives in
/// <see cref="FoundryIQ.FoundryIQLiveConformanceTests"/>
/// when the live vector store is configured. This suite verifies the
/// pipeline sends the scoped source set to Foundry via a call-recording
/// stub so the code path is exercised without a live vector store.
/// </summary>
public sealed class PerAgentBindingProviderParityTests
{
    private static KnowledgeSourcesOptions Sources(params (string name, string doc)[] entries)
    {
        var opts = new KnowledgeSourcesOptions();
        foreach ((string name, string doc) in entries)
        {
            opts.Named[name] = new KnowledgeSourceDefinition { Documents = [doc] };
        }
        return opts;
    }

    private static Dictionary<string, AgentDefinition> Agents(params (string key, bool enabled, string source)[] entries)
    {
        var map = new Dictionary<string, AgentDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, bool enabled, string source) in entries)
        {
            map[key] = new AgentDefinition
            {
                Key = key,
                Name = key,
                UseKnowledgeBase = enabled,
                KnowledgeBaseName = source,
            };
        }
        return map;
    }

    [Fact]
    public async Task InMemoryProvider_ScopedAgent_OnlySeesInScopeSources()
    {
        InMemoryKnowledgeBase kb = new(
            NullLoggerFactory.Instance.CreateLogger<InMemoryKnowledgeBase>(),
            Options.Create(new KnowledgeOptions()));
        await kb.IngestDocumentAsync("Planogram", "Apex planogram shelf-set anchor content.", "planogram.md");
        await kb.IngestDocumentAsync("Supplier", "Apex distributor fill rate SLA content.", "supplier.md");

        var registry = KnowledgeSourceRegistry.Build(
            Sources(("planogram", "planogram.md")),
            Agents(("planogram-agent", true, "planogram")));

        var provider = new RagContextProvider(kb, NullLogger<RagContextProvider>.Instance, sourceRegistry: registry);

        RagRetrievalOutcome outcome = await provider.GetContextForAgentAsync(
            "shelf anchor Apex distributor", userId: "u", agentKey: "planogram-agent");

        outcome.Enabled.Should().BeTrue();
        outcome.Scoped.Should().BeTrue();
        outcome.Context.Should().NotBeNullOrEmpty();
        outcome.Context.Should().Contain("Planogram");
        outcome.Context.Should().NotContain("Supplier", "scoped binding must exclude out-of-scope sources");
    }

    [Fact]
    public async Task HybridProviderShape_ScopedAgent_ForwardsSourceScopeToProvider()
    {
        var kb = new RecordingScopedKnowledgeBase(
            new SearchResult("p", "Planogram", "In-scope chunk.", 0.9, "planogram.md", 0));

        var registry = KnowledgeSourceRegistry.Build(
            Sources(("planogram", "planogram.md")),
            Agents(("planogram-agent", true, "planogram")));

        var provider = new RagContextProvider(kb, NullLogger<RagContextProvider>.Instance, sourceRegistry: registry);

        RagRetrievalOutcome outcome = await provider.GetContextForAgentAsync(
            "any query", userId: "u", agentKey: "planogram-agent");

        outcome.Scoped.Should().BeTrue();
        kb.LastScopedCallSources.Should().NotBeNull();
        kb.LastScopedCallSources.Should().BeEquivalentTo(["planogram.md"],
            "the RagContextProvider MUST forward the agent's declared source scope to the provider's scoped SearchAsync overload");
    }

    [Fact]
    public async Task ReadOnlyProviderShape_ScopedAgent_ForwardsSourceScopeToProvider()
    {
        // Foundry IQ (read-only) parity: even though we can't ingest, the
        // per-agent binding + RagContextProvider layer must still forward
        // the source scope. This proves the code path irrespective of
        // read/write capability.
        var kb = new RecordingScopedKnowledgeBase(
            new SearchResult("f", "Foundry Doc", "In-scope chunk.", 0.9, "playbook.md", 0))
        {
            SupportsMutation = false,
        };

        var registry = KnowledgeSourceRegistry.Build(
            Sources(("playbook", "playbook.md")),
            Agents(("playbook-agent", true, "playbook")));

        var provider = new RagContextProvider(kb, NullLogger<RagContextProvider>.Instance, sourceRegistry: registry);

        RagRetrievalOutcome outcome = await provider.GetContextForAgentAsync(
            "policy question", userId: "u", agentKey: "playbook-agent");

        outcome.Scoped.Should().BeTrue();
        kb.LastScopedCallSources.Should().BeEquivalentTo(["playbook.md"]);
    }

    /// <summary>
    /// Provider stub that records whether it was called through the scoped
    /// or unscoped overload and what sources were passed. Returns the pinned
    /// hits so the RagContextProvider produces a real outcome.
    /// </summary>
    private sealed class RecordingScopedKnowledgeBase(params SearchResult[] hits) : IKnowledgeBase
    {
        public IReadOnlyCollection<string>? LastScopedCallSources { get; private set; }
        public bool SupportsMutation { get; set; } = true;

        public KnowledgeBaseCapabilities GetCapabilities() => new(
            "RecordingStub", KnowledgeRelevanceKind.Hybrid, Persistent: true, RequiresCloud: true,
            new KnowledgeQuotas(10_000, 100_000, 25 * 1024 * 1024),
            "Stub. NOT comparable across providers.",
            SupportsMutation: SupportsMutation);

        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> IngestDocumentAsync(string title, string content, string source, CancellationToken ct = default) =>
            SupportsMutation
                ? Task.FromResult(Guid.NewGuid().ToString("N"))
                : throw new NotSupportedException("read-only");

        public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK = 5, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SearchResult>>(hits);

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            string query, int topK, IReadOnlyCollection<string>? sources, CancellationToken ct = default)
        {
            LastScopedCallSources = sources is null ? null : [.. sources];
            return Task.FromResult<IReadOnlyList<SearchResult>>(hits);
        }

        public Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DocumentInfo>>([]);

        public Task DeleteDocumentAsync(string documentId, CancellationToken ct = default) =>
            SupportsMutation ? Task.CompletedTask : throw new NotSupportedException("read-only");
    }
}

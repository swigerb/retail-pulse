using Azure.AI.Agents.Persistent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Rag;
using RetailPulse.Api.Rag.FoundryIQ;
using RetailPulse.Contracts.Rag;
using Xunit.Abstractions;

namespace RetailPulse.Tests.Rag.FoundryIQ;

/// <summary>
/// Fixed-query ranked-relevance sanity comparison for Foundry IQ vs the
/// in-repo InMemory provider. Per issue #104 contract:
///
/// <list type="bullet">
///   <item>Raw provider scores are NEVER compared across providers — score
///     ranges and semantics are provider-specific. We compare RANK ORDER
///     against a pre-labelled expected document set.</item>
///   <item>The report is informational — this test NEVER fails on quality.
///     It records Recall@3 per provider so operators can spot regression
///     during promotion, not gate a build on cross-provider parity.</item>
///   <item>When Foundry IQ is not configured the Foundry column is recorded
///     as skipped and the test still passes — the intent is transparency,
///     never a fabricated success.</item>
/// </list>
///
/// The corpus is a small shared set defined below. For InMemory we ingest
/// directly. For Foundry we run the same queries against the configured
/// vector store — the assumption is that the operator has pre-populated
/// the vector store with a semantically similar corpus. The comparison
/// uses paraphrased queries and reports Recall@3 as the honest signal.
/// </summary>
public sealed class FoundryIQRetrievalQualityComparisonTests(ITestOutputHelper output)
{
    private static readonly (string Title, string Content, string Source, string[] Concepts)[] Corpus =
    [
        ("Planogram Compliance", "Planogram compliance measures how faithfully store shelves match the merchandising plan. Auditors capture shelf photos and score adherence.", "std-01.md", ["planogram", "compliance", "audit"]),
        ("Cold Chain Monitoring", "Cold chain monitoring uses IoT sensors to detect temperature excursions in dairy, frozen, and produce aisles. Alerts trigger corrective action.", "std-02.md", ["cold chain", "temperature", "iot"]),
        ("Returns Handling", "Returns handling policies require SKU verification, condition grading, and restocking or salvage routing within 24 hours.", "std-03.md", ["returns", "restock", "salvage"]),
    ];

    private static readonly (string Query, string ExpectedSource)[] Queries =
    [
        ("How do stores measure shelf adherence to merchandising plans?", "std-01.md"),
        ("What monitors freezer temperature drift?", "std-02.md"),
        ("How are customer returns processed after receipt?", "std-03.md"),
    ];

    [Fact]
    public async Task RankedRelevance_ComparisonReport_IsInformationalOnly()
    {
        double inMemoryRecall = await MeasureInMemoryRecallAsync();
        double? foundryRecall = await TryMeasureFoundryRecallAsync();

        output.WriteLine("Ranked-relevance sanity comparison (Recall@3, informational only — no raw scores compared)");
        output.WriteLine($"  InMemory     : {inMemoryRecall:P0}");
        output.WriteLine(foundryRecall is null
            ? "  FoundryIQ    : SKIPPED (live environment not configured)"
            : $"  FoundryIQ    : {foundryRecall.Value:P0}");

        inMemoryRecall.Should().BeGreaterThanOrEqualTo(0.0);
    }

    private async Task<double> MeasureInMemoryRecallAsync()
    {
        IOptions<KnowledgeOptions> opts = Options.Create(new KnowledgeOptions());
        var kb = new InMemoryKnowledgeBase(NullLogger<InMemoryKnowledgeBase>.Instance, opts);
        foreach ((string title, string content, string source, _) in Corpus)
        {
            await kb.IngestDocumentAsync(title, content, source);
        }

        int hits = 0;
        foreach ((string query, string expectedSource) in Queries)
        {
            IReadOnlyList<SearchResult> results = await kb.SearchAsync(query, topK: 3);
            if (results.Take(3).Any(r => string.Equals(r.Source, expectedSource, StringComparison.OrdinalIgnoreCase)))
            {
                hits++;
            }
        }
        return (double)hits / Queries.Length;
    }

    private async Task<double?> TryMeasureFoundryRecallAsync()
    {
        if (!FoundryIQLiveTestConfig.IsConfigured(out _))
        {
            return null;
        }

        string endpoint = FoundryIQLiveTestConfig.ResolveEndpoint();
        var options = new FoundryIQOptions
        {
            ProjectEndpoint = endpoint,
            VectorStoreName = FoundryIQLiveTestConfig.ResolveVectorStoreName(),
            VectorStoreId = FoundryIQLiveTestConfig.ResolveVectorStoreId() ?? string.Empty,
            Model = FoundryIQLiveTestConfig.ResolveModel(),
            RetrievalAgentName = FoundryIQLiveTestConfig.ResolveRetrievalAgentName(),
            RetrievalAgentId = FoundryIQLiveTestConfig.ResolveRetrievalAgentId() ?? string.Empty,
            RequestTimeoutMs = 120_000,
        };
        var credential = new Azure.Identity.DefaultAzureCredential();
        var accessor = new FoundryClientAccessor(credential);
        PersistentAgentsClient client = accessor.GetOrCreate(endpoint);
        var iqClient = new FoundryIQClient(client, options);
        var resolver = new FoundryIQVectorStoreResolver(iqClient, options, NullLogger<FoundryIQVectorStoreResolver>.Instance);
        var agentProvider = new FoundryIQRetrievalAgentProvider(iqClient, resolver, options, NullLogger<FoundryIQRetrievalAgentProvider>.Instance);
        var kb = new FoundryIQKnowledgeBase(
            iqClient, resolver, agentProvider, options,
            new KnowledgeOptions(),
            new RecordingCostTracker(),
            NullLogger<FoundryIQKnowledgeBase>.Instance);

        int hits = 0;
        foreach ((string query, string expectedSource) in Queries)
        {
            IReadOnlyList<SearchResult> results;
            try
            {
                results = await kb.SearchAsync(query, topK: 3);
            }
            catch (KnowledgeProviderUnavailableException ex)
            {
                output.WriteLine($"  FoundryIQ query '{query}' unavailable: {ex.Message}");
                return null;
            }
            if (results.Take(3).Any(r =>
                r.Source.Contains(expectedSource.Split('.')[0], StringComparison.OrdinalIgnoreCase)))
            {
                hits++;
            }
        }
        return (double)hits / Queries.Length;
    }
}

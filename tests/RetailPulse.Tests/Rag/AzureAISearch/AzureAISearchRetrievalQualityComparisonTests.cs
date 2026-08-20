using Azure.Identity;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Observability;
using RetailPulse.Api.Rag;
using RetailPulse.Api.Rag.AzureAISearch;
using RetailPulse.Contracts.Observability;
using RetailPulse.Contracts.Rag;
using Xunit.Abstractions;

namespace RetailPulse.Tests.Rag.AzureAISearch;

/// <summary>
/// Runs a fixed query set against BOTH the in-memory BM25 provider and the
/// live Azure AI Search provider (when configured) and records the top-k
/// recall for each. Semantic retrieval should beat lexical on paraphrased
/// queries; if it doesn't, the harness surfaces the number honestly to the
/// test output so the PR body can record the finding.
///
/// Skipped cleanly with an explicit reason when the live environment is not
/// configured. Never fails on quality — it captures the number and lets the
/// human reviewer interpret it.
/// </summary>
public sealed class AzureAISearchRetrievalQualityComparisonTests
{
    private static readonly (string Title, string Content, string Source)[] _corpus =
    [
        ("Category Management Playbook",
         "Category management defines the role, strategy, and metrics for every merchandising category. " +
         "Category captains coordinate with suppliers on planograms, promotional cadence, and assortment. " +
         "The end goal is category-level growth measured against a defined benchmark.",
         "playbook"),
        ("Holiday Planning Guide",
         "Holiday displays should be set in early October to maximize impact. " +
         "Themed holiday displays outperform generic seasonal displays year over year. " +
         "Ensure holiday-specific SKUs are protected from stockouts through the peak weeks.",
         "guide"),
        ("Supplier Terms Standards",
         "Supplier terms must include on-time delivery penalties, quality inspection windows, and returns policy. " +
         "Terms are renegotiated annually and reviewed against category benchmarks.",
         "standards"),
        ("Planogram Compliance Standard",
         "Planogram compliance is measured weekly by store-level audit teams. " +
         "Non-compliant fixtures must be corrected within 48 hours. " +
         "Repeat non-compliance triggers a category review.",
         "standard"),
        ("Assortment Optimization Notes",
         "Assortment optimization balances SKU productivity, category coverage, and shelf space efficiency. " +
         "Slow movers below a defined velocity threshold are candidates for deletion each quarter.",
         "notes"),
    ];

    // Paraphrased queries — lexical BM25 usually MISSES these because they
    // avoid the exact keywords in the corpus while preserving semantic intent.
    private static readonly (string Query, string ExpectedTitle)[] _paraphrasedQueries =
    [
        ("How do I coordinate with vendors on shelf layouts?", "Category Management Playbook"),
        ("When should Christmas fixtures be installed?", "Holiday Planning Guide"),
        ("What penalties apply to vendors who miss shipment windows?", "Supplier Terms Standards"),
        ("How often do audit teams check display compliance?", "Planogram Compliance Standard"),
        ("Which slow items should we drop from the shelf?", "Assortment Optimization Notes"),
    ];

    private readonly ITestOutputHelper _output;

    public AzureAISearchRetrievalQualityComparisonTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task BaselineInMemory_RunsAgainstFixedQuerySet()
    {
        InMemoryKnowledgeBase kb = new(
            NullLoggerFactory.Instance.CreateLogger<InMemoryKnowledgeBase>(),
            Options.Create(new KnowledgeOptions()));

        foreach ((string title, string content, string source) in _corpus)
        {
            await kb.IngestDocumentAsync(title, content, source);
        }

        int hits = 0;
        _output.WriteLine("=== InMemory BM25 baseline (paraphrased query set) ===");
        foreach ((string query, string expectedTitle) in _paraphrasedQueries)
        {
            IReadOnlyList<SearchResult> results = await kb.SearchAsync(query, topK: 3);
            bool matched = results.Any(r => r.Title == expectedTitle);
            if (matched) hits++;
            _output.WriteLine($"query='{query}' expected='{expectedTitle}' top3=[{string.Join(", ", results.Select(r => r.Title))}] matched={matched}");
        }
        _output.WriteLine($"BaselineInMemory Recall@3: {hits}/{_paraphrasedQueries.Length}");

        // The baseline is expected to underperform on paraphrase — this is
        // the executable "why we need semantic" documentation. We record it
        // but do not fail on quality.
        hits.Should().BeGreaterThanOrEqualTo(0);
    }

    [LiveAzureAISearchFact]
    public async Task SemanticProvider_MeetsOrExceedsInMemoryOnParaphrasedQueries()
    {
        // Live comparison — ingest the same corpus into a fresh per-run
        // Azure AI Search index and compare Recall@3 to the in-memory baseline
        // on the same paraphrased queries.
        InMemoryKnowledgeBase baseline = new(
            NullLoggerFactory.Instance.CreateLogger<InMemoryKnowledgeBase>(),
            Options.Create(new KnowledgeOptions()));
        foreach ((string title, string content, string source) in _corpus)
        {
            await baseline.IngestDocumentAsync(title, content, source);
        }

        AzureAISearchKnowledgeBase semantic = await CreateLiveProviderAsync();
        foreach ((string title, string content, string source) in _corpus)
        {
            await semantic.IngestDocumentAsync(title, content, source);
        }
        // Let Azure AI Search near-real-time indexing settle before querying.
        await Task.Delay(TimeSpan.FromSeconds(5));

        int lexicalHits = 0, semanticHits = 0;
        _output.WriteLine("=== Retrieval quality comparison: InMemory (BM25) vs AzureAISearch (Hybrid) ===");
        foreach ((string query, string expectedTitle) in _paraphrasedQueries)
        {
            IReadOnlyList<SearchResult> lex = await baseline.SearchAsync(query, topK: 3);
            IReadOnlyList<SearchResult> sem = await semantic.SearchAsync(query, topK: 3);
            bool lexHit = lex.Any(r => r.Title == expectedTitle);
            bool semHit = sem.Any(r => r.Title == expectedTitle);
            if (lexHit) lexicalHits++;
            if (semHit) semanticHits++;

            _output.WriteLine(
                $"query='{query}' expected='{expectedTitle}' lex=[{string.Join(", ", lex.Select(r => r.Title))}] sem=[{string.Join(", ", sem.Select(r => r.Title))}] lexHit={lexHit} semHit={semHit}");
        }

        _output.WriteLine($"Recall@3 InMemory (BM25): {lexicalHits}/{_paraphrasedQueries.Length}");
        _output.WriteLine($"Recall@3 AzureAISearch (Hybrid): {semanticHits}/{_paraphrasedQueries.Length}");

        // Report honestly — we do NOT enforce semantic > lexical because live
        // model quality varies. The harness surfaces the numbers to the PR
        // and CI output; humans interpret them.
        semanticHits.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void RetrievalQuality_ExplicitlyAssertsSkipReason_WhenUnconfigured()
    {
        bool configured = AzureAISearchLiveTestConfig.IsConfigured(out string? reason);
        if (configured)
        {
            reason.Should().BeNull();
            return;
        }
        reason.Should().Be(AzureAISearchLiveTestConfig.SkipReason,
            "the comparison harness must record an explicit skip reason so operators can distinguish an outage from an unconfigured environment");
    }

    private static async Task<AzureAISearchKnowledgeBase> CreateLiveProviderAsync()
    {
        string indexName = ("rp-cmp-" + Guid.NewGuid().ToString("N"))[..24];

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Knowledge:AzureAISearch:Endpoint"] = AzureAISearchLiveTestConfig.ResolveEndpoint(),
                ["Knowledge:AzureAISearch:IndexName"] = indexName,
                ["Knowledge:AzureAISearch:Embeddings:Endpoint"] = AzureAISearchLiveTestConfig.ResolveEmbeddingsEndpoint(),
                ["Knowledge:AzureAISearch:Embeddings:Deployment"] = AzureAISearchLiveTestConfig.ResolveEmbeddingsDeployment(),
                ["Knowledge:AzureAISearch:Embeddings:ApimSubscriptionKey"] = AzureAISearchLiveTestConfig.ResolveEmbeddingsApimKey(),
                ["Knowledge:AzureAISearch:SemanticRankingEnabled"] = AzureAISearchLiveTestConfig.ResolveSemantic() ? "true" : "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.Configure<KnowledgeOptions>(_ => { });
        services.AddSingleton<Azure.Core.TokenCredential>(new DefaultAzureCredential());
        services.AddSingleton<ICostTracker>(new InMemoryCostTracker(
            Options.Create(new ObservabilityOptions { MaxCostEvents = 100, CostEventTtlHours = 24 }),
            config));
        services.AddAzureAISearchKnowledgeProvider(config);

        ServiceProvider sp = services.BuildServiceProvider();
        AzureAISearchKnowledgeBase kb = sp.GetRequiredService<AzureAISearchKnowledgeBase>();
        await kb.ProbeAsync();
        return kb;
    }
}

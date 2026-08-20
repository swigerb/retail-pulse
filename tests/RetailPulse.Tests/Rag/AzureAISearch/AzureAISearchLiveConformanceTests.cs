using Azure.Identity;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Observability;
using RetailPulse.Api.Rag.AzureAISearch;
using RetailPulse.Contracts.Observability;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Tests.Rag.AzureAISearch;

/// <summary>
/// Live conformance for the Azure AI Search provider. Mirrors the invariants
/// codified in <see cref="KnowledgeBaseConformanceTests"/> but decorates each
/// case with <see cref="LiveAzureAISearchFactAttribute"/> so xunit skips
/// cleanly (with an explicit reason string) when the environment variables
/// documented on <see cref="AzureAISearchLiveTestConfig"/> are not set.
///
/// A companion always-run test asserts the skip is explicit — the CI output
/// distinguishes "unconfigured, skipped" from "configured but silently no-op".
///
/// The suite provisions a per-run index name so concurrent CI runs cannot
/// clobber each other, and does not delete the index at teardown so operators
/// can inspect state. Follow <c>docs/rag/azure-ai-search-index.md</c> to
/// reset a live index.
/// </summary>
public sealed class AzureAISearchLiveConformanceTests
{
    private static async Task<AzureAISearchKnowledgeBase> CreateAsync()
    {
        string indexName = ("rp-" + Guid.NewGuid().ToString("N"))[..24];

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

    [LiveAzureAISearchFact]
    public async Task ProbeAsync_HealthyProvider_Completes()
    {
        AzureAISearchKnowledgeBase kb = await CreateAsync();
        Func<Task> probe = () => kb.ProbeAsync();
        await probe.Should().NotThrowAsync();
    }

    [LiveAzureAISearchFact]
    public async Task GetCapabilities_ReportsHybridAndPersistent()
    {
        AzureAISearchKnowledgeBase kb = await CreateAsync();
        KnowledgeBaseCapabilities caps = kb.GetCapabilities();

        caps.ProviderName.Should().Be(AzureAISearchKnowledgeBase.ProviderName);
        caps.Persistent.Should().BeTrue();
        caps.RequiresCloud.Should().BeTrue();
        caps.Relevance.Should().Be(KnowledgeRelevanceKind.Hybrid);
        caps.ScoreSemantics.Should().Contain("not comparable");
    }

    [LiveAzureAISearchFact]
    public async Task Ingest_ThenSearch_FindsRelevantContent()
    {
        AzureAISearchKnowledgeBase kb = await CreateAsync();
        await kb.IngestDocumentAsync(
            "Holiday Planning",
            "Holiday displays should go up in early October for maximum impact. " +
            "Themed holiday displays outperform generic seasonal displays.",
            "conformance-live");

        // Give the index a moment for near-real-time indexing.
        await Task.Delay(TimeSpan.FromSeconds(3));

        IReadOnlyList<SearchResult> results = await kb.SearchAsync("holiday");
        results.Should().NotBeEmpty();
        results.Should().OnlyContain(r => r.Score >= 0);
    }

    [LiveAzureAISearchFact]
    public async Task DeleteDocument_RemovesFromListAndSearch()
    {
        AzureAISearchKnowledgeBase kb = await CreateAsync();
        string id = await kb.IngestDocumentAsync(
            "Live-Deletable",
            "Uniquetermxyzzy content that is only in this document.",
            "conformance-live");

        await Task.Delay(TimeSpan.FromSeconds(2));
        await kb.DeleteDocumentAsync(id);
        await Task.Delay(TimeSpan.FromSeconds(3));

        IReadOnlyList<SearchResult> hits = await kb.SearchAsync("uniquetermxyzzy");
        hits.Should().NotContain(r => r.DocumentId == id);
    }

    /// <summary>
    /// Explicit skip assertion. Always runs. Asserts the environment either
    /// exposes a real endpoint (so the [LiveAzureAISearchFact] cases above
    /// executed) or that the skip reason string is the documented one — a
    /// silent no-op is never a valid outcome.
    /// </summary>
    [Fact]
    public void LiveConformance_ExplicitlyAssertsSkipReason_WhenUnconfigured()
    {
        bool configured = AzureAISearchLiveTestConfig.IsConfigured(out string? reason);

        if (configured)
        {
            reason.Should().BeNull();
            return;
        }

        reason.Should().Be(AzureAISearchLiveTestConfig.SkipReason,
            "live conformance must record an explicit skip reason so operators distinguish an outage from an unconfigured environment");
    }
}

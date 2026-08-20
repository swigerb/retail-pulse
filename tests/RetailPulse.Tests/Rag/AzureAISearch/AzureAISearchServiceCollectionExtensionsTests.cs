using Azure.Core;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Observability;
using RetailPulse.Api.Rag;
using RetailPulse.Api.Rag.AzureAISearch;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Tests.Rag.AzureAISearch;

/// <summary>
/// DI-shape contract. When Knowledge:AzureAISearch:Endpoint is blank the
/// extension MUST be a no-op — no Search SDK client, no HTTP client, no
/// contribution to the provider registry — so the default demo path is byte-
/// for-byte identical to the InMemory-only baseline. When the endpoint is
/// configured the extension registers the provider and contributes an
/// AzureAISearch factory to the registry.
/// </summary>
public class AzureAISearchServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAzureAISearchKnowledgeProvider_BlankEndpoint_NoRegistrations()
    {
        ServiceCollection services = BuildBaselineServices();
        int before = services.Count;
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        services.AddAzureAISearchKnowledgeProvider(config);

        services.Count.Should().Be(before,
            "blank endpoint means the provider is disabled — no service should be registered");
        services.Should().NotContain(sd => sd.ServiceType == typeof(SearchClient));
        services.Should().NotContain(sd => sd.ServiceType == typeof(SearchIndexClient));
        services.Should().NotContain(sd => sd.ServiceType == typeof(AzureAISearchKnowledgeBase));
        services.Should().NotContain(sd => sd.ServiceType == typeof(IKnowledgeProviderContribution));
    }

    [Fact]
    public void AddAzureAISearchKnowledgeProvider_BlankEndpoint_DoesNotRegisterEmbeddingsHttpClient()
    {
        ServiceCollection services = BuildBaselineServices();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        services.AddAzureAISearchKnowledgeProvider(config);

        using ServiceProvider sp = services.BuildServiceProvider();
        // The named HttpClient is only registered on the enabled path.
        // Requesting it should NOT resolve into a client that has our base
        // address / handler pipeline; verify by asserting the extension left
        // the DI graph exactly as our baseline (no IHttpClientFactory added).
        sp.GetService<IHttpClientFactory>().Should().BeNull(
            "no HTTP factory should be materialized on the disabled path");
    }

    [Fact]
    public void AddAzureAISearchKnowledgeProvider_Configured_RegistersProviderAndContribution()
    {
        ServiceCollection services = BuildBaselineServices();
        // Substitute the token credential BEFORE the extension so tests do
        // not need a real Azure identity — the extension uses TryAddSingleton.
        services.AddSingleton<TokenCredential>(new FakeTokenCredential());
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Knowledge:AzureAISearch:Endpoint"] = "https://mysearch.search.windows.net",
                ["Knowledge:AzureAISearch:IndexName"] = "retail-pulse-test",
                ["Knowledge:AzureAISearch:Embeddings:Endpoint"] = "https://apim.example.com/inference",
                ["Knowledge:AzureAISearch:Embeddings:Deployment"] = "text-embedding-3-small",
                ["Knowledge:AzureAISearch:Embeddings:Dimensions"] = "8",
            })
            .Build();

        services.AddAzureAISearchKnowledgeProvider(config);

        using ServiceProvider sp = services.BuildServiceProvider();
        AzureAISearchKnowledgeBase kb = sp.GetRequiredService<AzureAISearchKnowledgeBase>();
        kb.GetCapabilities().ProviderName.Should().Be(AzureAISearchKnowledgeBase.ProviderName);
        kb.GetCapabilities().Persistent.Should().BeTrue();
        kb.GetCapabilities().RequiresCloud.Should().BeTrue();

        IEnumerable<IKnowledgeProviderContribution> contributions =
            sp.GetServices<IKnowledgeProviderContribution>();
        contributions.Should().ContainSingle();

        var registry = new KnowledgeProviderRegistry();
        registry.Register(KnowledgeProviderMode.InMemory, _ =>
            throw new InvalidOperationException("in-memory factory should not be invoked here"));
        foreach (IKnowledgeProviderContribution c in contributions)
        {
            c.Register(registry);
        }
        registry.IsRegistered(KnowledgeProviderMode.AzureAISearch).Should().BeTrue();
    }

    [Fact]
    public void AddAzureAISearchKnowledgeProvider_ConfiguredButInvalid_FailsFast()
    {
        ServiceCollection services = BuildBaselineServices();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Knowledge:AzureAISearch:Endpoint"] = "https://mysearch.search.windows.net",
                // No Embeddings:Endpoint on purpose — must fail fast.
            })
            .Build();

        Action act = () => services.AddAzureAISearchKnowledgeProvider(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Embeddings*Endpoint*");
    }

    private static ServiceCollection BuildBaselineServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.Configure<KnowledgeOptions>(_ => { });
        services.Configure<ObservabilityOptions>(_ => { });
        services.AddSingleton<ICostTracker>(new InMemoryCostTracker(
            Options.Create(new ObservabilityOptions
            {
                MaxCostEvents = 100,
                CostEventTtlHours = 24,
                MaxSessions = 10,
                MaxMessagesPerSession = 10,
            }),
            new ConfigurationBuilder().Build()));
        return services;
    }

    private sealed class FakeTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("fake-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }
}

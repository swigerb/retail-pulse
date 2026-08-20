using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Resilience;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Api.Rag.AzureAISearch;

/// <summary>
/// Opt-in registration of the Azure AI Search knowledge provider.
///
/// The provider is FULLY OPTIONAL. When the <c>Knowledge:AzureAISearch:Endpoint</c>
/// configuration value is blank, this extension is a no-op: no Search SDK
/// client, embeddings HTTP client, credential, or token provider is
/// materialized, and the <see cref="KnowledgeProviderRegistry"/> stays
/// InMemory-only. Selecting <see cref="KnowledgeProviderMode.AzureAISearch"/>
/// without configuring the endpoint fails startup with the shared
/// unregistered-mode message from <see cref="KnowledgeProviderRegistry"/>.
/// </summary>
public static class AzureAISearchServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Azure AI Search knowledge provider (idempotent).
    ///
    /// Behaviour by configuration state:
    /// <list type="bullet">
    ///   <item><description>Blank endpoint — no registrations added. Default demo path unchanged.</description></item>
    ///   <item><description>Endpoint set — registers <see cref="AzureAISearchKnowledgeBase"/> and the
    ///   supporting Search SDK / embeddings HTTP path; adds the factory to
    ///   <see cref="KnowledgeProviderRegistry"/> so
    ///   <c>Mode=AzureAISearch</c> resolves it.</description></item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddAzureAISearchKnowledgeProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new AzureAISearchOptions();
        configuration.GetSection(AzureAISearchOptions.SectionName).Bind(options);
        if (!options.IsConfigured)
        {
            // Fully-optional gate. The registration is a no-op so selecting a
            // non-InMemory mode without wiring the provider fails loudly via
            // KnowledgeProviderRegistry.Create — never a silent degradation.
            return services;
        }

        options.ValidateEnabled();

        // Bind the strongly-typed options for consumers and register a
        // singleton copy for direct-inject sites (index-schema tests, DI
        // shape assertions).
        services.Configure<AzureAISearchOptions>(
            configuration.GetSection(AzureAISearchOptions.SectionName));
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<AzureAISearchOptions>>().Value);

        // Single credential for the whole process — the Search SDK, the
        // embeddings HTTP path, and any other MI-authenticated caller share
        // one login stream. Kept as TokenCredential so tests can substitute
        // deterministic credentials.
        services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
        services.TryAddSingleton<CognitiveServicesTokenProvider>();

        // Named HTTP client with bounded timeout + retry + circuit breaker
        // shared between the raw embeddings path and any future direct APIM
        // caller — one breaker per external dependency.
        services.AddHttpClient(ApimEmbeddingClient.HttpClientName, (sp, client) =>
        {
            AzureAISearchOptions opts = sp.GetRequiredService<AzureAISearchOptions>();
            client.BaseAddress = new Uri(TrimTrailingSlash(opts.Embeddings.Endpoint!) + "/", UriKind.Absolute);
            client.Timeout = TimeSpan.FromMilliseconds(Math.Max(1_000, opts.Embeddings.TimeoutMs * 4));
        }).AddKnowledgeEmbeddingsResilienceHandler(options.Embeddings.TimeoutMs);

        services.TryAddSingleton(sp =>
        {
            IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
            HttpClient http = factory.CreateClient(ApimEmbeddingClient.HttpClientName);
            return new ApimEmbeddingClient(
                http,
                sp.GetRequiredService<AzureAISearchOptions>(),
                sp.GetRequiredService<ICostTracker>(),
                sp.GetRequiredService<ILogger<ApimEmbeddingClient>>(),
                sp.GetRequiredService<CognitiveServicesTokenProvider>());
        });

        services.TryAddSingleton(sp =>
        {
            AzureAISearchOptions opts = sp.GetRequiredService<AzureAISearchOptions>();
            TokenCredential credential = sp.GetRequiredService<TokenCredential>();
            var clientOptions = new SearchClientOptions
            {
                Retry =
                {
                    NetworkTimeout = TimeSpan.FromMilliseconds(opts.RequestTimeoutMs),
                    MaxRetries = 3,
                    Mode = Azure.Core.RetryMode.Exponential,
                },
            };
            return new SearchIndexClient(new Uri(opts.Endpoint!, UriKind.Absolute), credential, clientOptions);
        });

        services.TryAddSingleton(sp =>
        {
            AzureAISearchOptions opts = sp.GetRequiredService<AzureAISearchOptions>();
            TokenCredential credential = sp.GetRequiredService<TokenCredential>();
            var clientOptions = new SearchClientOptions
            {
                Retry =
                {
                    NetworkTimeout = TimeSpan.FromMilliseconds(opts.RequestTimeoutMs),
                    MaxRetries = 3,
                    Mode = Azure.Core.RetryMode.Exponential,
                },
            };
            return new SearchClient(new Uri(opts.Endpoint!, UriKind.Absolute), opts.IndexName, credential, clientOptions);
        });

        services.TryAddSingleton<AzureAISearchKnowledgeBase>(sp => new AzureAISearchKnowledgeBase(
            sp.GetRequiredService<SearchIndexClient>(),
            sp.GetRequiredService<SearchClient>(),
            sp.GetRequiredService<ApimEmbeddingClient>(),
            sp.GetRequiredService<AzureAISearchOptions>(),
            sp.GetRequiredService<IOptions<KnowledgeOptions>>().Value,
            sp.GetRequiredService<ILogger<AzureAISearchKnowledgeBase>>()));

        // Contribute the factory to the shared KnowledgeProviderRegistry via
        // the IKnowledgeProviderContribution seam so selecting
        // Knowledge:Provider:Mode=AzureAISearch resolves this KB.
        services.AddSingleton<IKnowledgeProviderContribution, AzureAISearchProviderContribution>();

        return services;
    }

    private static string TrimTrailingSlash(string value) =>
        value.EndsWith('/') ? value[..^1] : value;
}

/// <summary>
/// Contributes the Azure AI Search provider factory to the shared
/// <see cref="KnowledgeProviderRegistry"/>. Consumed by the registry singleton
/// factory in <c>Program.cs</c>.
/// </summary>
internal sealed class AzureAISearchProviderContribution : IKnowledgeProviderContribution
{
    public void Register(KnowledgeProviderRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(
            KnowledgeProviderMode.AzureAISearch,
            sp => sp.GetRequiredService<AzureAISearchKnowledgeBase>());
    }
}

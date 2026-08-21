using Azure.Core;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.AI.ContentSafety;
using Azure.AI.Agents.Persistent;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Api.Rag;
using RetailPulse.Api.Rag.AzureAISearch;
using RetailPulse.Api.Rag.FoundryIQ;
using RetailPulse.Contracts.Guardrails;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Tests.Rag.Optionality;

/// <summary>
/// Highest-priority Wave 5 optionality proof (issue #107). Mirrors the exact
/// <c>Program.cs</c> knowledge-provider + Content Safety wiring under a
/// zero-cloud configuration and asserts:
///
/// <list type="number">
///   <item>The provider registry contains only the InMemory factory.</item>
///   <item>No cloud SDK client type is constructible from the container -
///     specifically <see cref="SearchClient"/>, <see cref="SearchIndexClient"/>,
///     <see cref="AzureAISearchKnowledgeBase"/>, <see cref="AzureAISearchOptions"/>,
///     <see cref="ApimEmbeddingClient"/>, <see cref="CognitiveServicesTokenProvider"/>,
///     <see cref="FoundryIQKnowledgeBase"/>, <see cref="FoundryIQOptions"/>,
///     <see cref="FoundryClientAccessor"/>, <see cref="PersistentAgentsClient"/>,
///     <see cref="ContentSafetyClient"/>, <see cref="ContentSafetyTokenProvider"/>,
///     <see cref="TokenCredential"/> (the shared credential none of the
///     optional providers introduced).</item>
///   <item>The active <see cref="IContentSafetyEvaluator"/> is the
///     <see cref="NoOpContentSafetyEvaluator"/> - no Azure client, no HTTP
///     handler, no bearer token acquisition path is materialized.</item>
///   <item>The active <see cref="IKnowledgeBase"/> resolves to the InMemory
///     provider through the degradation decorator and its probe completes
///     without any external I/O.</item>
/// </list>
///
/// The intent is to prove - at DI-graph shape level - that a Wave-5 default
/// deployment neither constructs nor contacts any cloud client, satisfying
/// the "no cloud client constructed OR contacted when its provider is
/// disabled" acceptance criterion. Existing per-provider disabled-startup
/// tests cover each provider in isolation; this test covers the union.
/// </summary>
public sealed class ZeroCloudDependencyStartupTests
{
    [Fact]
    public async Task DefaultConfiguration_AllOptionalProvidersDisabled_NoCloudClientMaterialized()
    {
        // Blank Knowledge and Guardrails sections - the "runs on a laptop"
        // demo path documented in appsettings.json.
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Explicit even though blank == default; the explicit form
                // exercises the parse-path documented in the ADR.
                ["Knowledge:Provider:Mode"] = "InMemory",
                ["Knowledge:Provider:Degradation"] = "FailLoud",
            })
            .Build();

        using ServiceProvider sp = BuildZeroCloudProvider(config);

        // Provider registry: only InMemory
        KnowledgeProviderRegistry registry = sp.GetRequiredService<KnowledgeProviderRegistry>();
        registry.RegisteredModes.Should().ContainSingle().Which.Should().Be(KnowledgeProviderMode.InMemory);
        registry.IsRegistered(KnowledgeProviderMode.AzureAISearch).Should().BeFalse();
        registry.IsRegistered(KnowledgeProviderMode.FoundryIQ).Should().BeFalse();

        // Azure AI Search DI graph: unmaterialized
        sp.GetService<AzureAISearchKnowledgeBase>().Should().BeNull();
        sp.GetService<AzureAISearchOptions>().Should().BeNull();
        sp.GetService<ApimEmbeddingClient>().Should().BeNull();
        sp.GetService<CognitiveServicesTokenProvider>().Should().BeNull();
        sp.GetService<SearchClient>().Should().BeNull();
        sp.GetService<SearchIndexClient>().Should().BeNull();

        // Foundry IQ DI graph: unmaterialized
        sp.GetService<FoundryIQKnowledgeBase>().Should().BeNull();
        sp.GetService<FoundryIQOptions>().Should().BeNull();
        sp.GetService<FoundryClientAccessor>().Should().BeNull();
        sp.GetService<PersistentAgentsClient>().Should().BeNull();

        // Content Safety: NoOp evaluator, no Azure SDK client
        sp.GetRequiredService<IContentSafetyEvaluator>()
            .Should().BeOfType<NoOpContentSafetyEvaluator>();
        sp.GetService<ContentSafetyClient>().Should().BeNull();
        sp.GetService<ContentSafetyTokenProvider>().Should().BeNull();

        // No shared TokenCredential was registered by any optional module -
        // this is the shared credential none of the disabled providers wired.
        sp.GetService<TokenCredential>().Should().BeNull(
            "no optional provider is enabled, so no cloud-facing TokenCredential should exist");

        // Active provider path: InMemory through the degradation decorator.
        DegradingKnowledgeBase degrading = sp.GetRequiredService<DegradingKnowledgeBase>();
        degrading.ActiveProviderName.Should().Be(InMemoryKnowledgeBase.ProviderName);
        degrading.DegradationMode.Should().Be(KnowledgeDegradationMode.FailLoud);
        degrading.PrimaryReplacedByFallback.Should().BeFalse();

        // Probe completes without external I/O.
        await degrading.ProbeAsync();
    }

    [Fact]
    public void SelectingAzureAISearch_WithoutEnabling_FailsLoud_NoSilentNoOp()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Knowledge:Provider:Mode"] = "AzureAISearch",
            })
            .Build();

        using ServiceProvider sp = BuildZeroCloudProvider(config);

        Action act = () => _ = sp.GetRequiredService<DegradingKnowledgeBase>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not registered*")
            .WithMessage("*AzureAISearch*");
    }

    [Fact]
    public void SelectingFoundryIQ_WithoutEnabling_FailsLoud_NoSilentNoOp()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Knowledge:Provider:Mode"] = "FoundryIQ",
            })
            .Build();

        using ServiceProvider sp = BuildZeroCloudProvider(config);

        Action act = () => _ = sp.GetRequiredService<DegradingKnowledgeBase>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not registered*")
            .WithMessage("*FoundryIQ*");
    }

    private static ServiceProvider BuildZeroCloudProvider(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(config);
        services.Configure<KnowledgeOptions>(config.GetSection(KnowledgeOptions.SectionName));
        services.Configure<KnowledgeProviderOptions>(
            config.GetSection(KnowledgeProviderOptions.SectionName));

        // HttpClientFactory is a Content-Safety dependency on the enabled
        // path. We register it on the disabled path too to prove the shape:
        // no ContentSafetyClient is ever resolved from it.
        services.AddHttpClient();

        // Optional providers with blank configuration (the disabled path).
        services.AddAzureAISearchKnowledgeProvider(config);
        services.AddFoundryIQKnowledgeProvider(config);

        // Content Safety with Enabled=false (the disabled path).
        var guardrails = new GuardrailsConfig();
        guardrails.ContentSafety.Enabled = false;
        services.AddSingleton(guardrails);
        services.AddContentSafety(guardrails.ContentSafety);

        // Knowledge base wiring identical to Program.cs.
        services.AddSingleton<InMemoryKnowledgeBase>();
        services.AddSingleton(sp =>
        {
            var registry = new KnowledgeProviderRegistry();
            registry.Register(
                KnowledgeProviderMode.InMemory,
                s => s.GetRequiredService<InMemoryKnowledgeBase>());
            foreach (IKnowledgeProviderContribution c in sp.GetServices<IKnowledgeProviderContribution>())
            {
                c.Register(registry);
            }
            return registry;
        });
        services.AddSingleton<KnowledgeProviderSelector>();
        services.AddSingleton(sp =>
        {
            KnowledgeProviderSelector selector = sp.GetRequiredService<KnowledgeProviderSelector>();
            IKnowledgeBase primary = selector.CreatePrimary(sp);
            InMemoryKnowledgeBase fallback = sp.GetRequiredService<InMemoryKnowledgeBase>();
            return new DegradingKnowledgeBase(
                primary,
                fallback,
                selector.ResolveDegradation(),
                sp.GetRequiredService<ILogger<DegradingKnowledgeBase>>());
        });

        return services.BuildServiceProvider();
    }
}

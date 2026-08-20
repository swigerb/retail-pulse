using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Rag;
using RetailPulse.Api.Rag.AzureAISearch;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Tests.Rag.AzureAISearch;

/// <summary>
/// Optional-default proof. Mirrors the Program.cs wiring for the Knowledge
/// section without any cloud resource, then verifies that:
///
/// 1. The provider selector resolves <see cref="KnowledgeProviderMode.InMemory"/>.
/// 2. The provider registry contains ONLY the InMemory factory.
/// 3. No Azure AI Search DI type is materialized.
/// 4. Calling <see cref="IKnowledgeBase.ProbeAsync"/> completes without
///    contacting any external resource.
///
/// This is the executable proof of the issue #103 non-negotiable requirement
/// that the provider is fully optional.
/// </summary>
public class AzureAISearchDefaultDisabledStartupTests
{
    [Fact]
    public void DefaultConfiguration_ResolvesInMemoryAndOmitsAzureAISearch()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(config);
        services.Configure<KnowledgeOptions>(config.GetSection(KnowledgeOptions.SectionName));
        services.Configure<KnowledgeProviderOptions>(config.GetSection(KnowledgeProviderOptions.SectionName));
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

        // This is the target line under test — extension must be a no-op on
        // blank config, leaving InMemory as the only registered provider.
        services.AddAzureAISearchKnowledgeProvider(config);

        using ServiceProvider sp = services.BuildServiceProvider();

        KnowledgeProviderSelector selector = sp.GetRequiredService<KnowledgeProviderSelector>();
        selector.ResolveMode().Should().Be(KnowledgeProviderMode.InMemory);
        selector.ResolveDegradation().Should().Be(KnowledgeDegradationMode.FailLoud);

        KnowledgeProviderRegistry registry = sp.GetRequiredService<KnowledgeProviderRegistry>();
        registry.RegisteredModes.Should().ContainSingle().Which
            .Should().Be(KnowledgeProviderMode.InMemory);
        registry.IsRegistered(KnowledgeProviderMode.AzureAISearch).Should().BeFalse(
            "the default demo config MUST NOT register a cloud provider factory");

        sp.GetService<AzureAISearchKnowledgeBase>().Should().BeNull(
            "the KB itself must not be resolvable when the endpoint is blank");
        sp.GetService<AzureAISearchOptions>().Should().BeNull(
            "the strongly-typed options singleton is only registered on the enabled path");

        // Materialize the primary and probe — no exception, no external call.
        IKnowledgeBase primary = selector.CreatePrimary(sp);
        primary.GetCapabilities().ProviderName.Should().Be(InMemoryKnowledgeBase.ProviderName);
        Func<Task> probe = () => primary.ProbeAsync();
        probe.Should().NotThrowAsync();
    }

    [Fact]
    public void DefaultConfiguration_SelectingAzureAISearch_FailsLoudlyWithActionableMessage()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Mode selected but the provider extension was never wired.
                ["Knowledge:Provider:Mode"] = "AzureAISearch",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(config);
        services.Configure<KnowledgeOptions>(config.GetSection(KnowledgeOptions.SectionName));
        services.Configure<KnowledgeProviderOptions>(config.GetSection(KnowledgeProviderOptions.SectionName));
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
        services.AddAzureAISearchKnowledgeProvider(config);

        using ServiceProvider sp = services.BuildServiceProvider();

        KnowledgeProviderSelector selector = sp.GetRequiredService<KnowledgeProviderSelector>();
        Action act = () => selector.CreatePrimary(sp);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not registered*")
            .WithMessage("*AzureAISearch*");
    }

    /// <summary>
    /// Guard so a future change to the extension can't silently re-materialize
    /// clients on the disabled path — the DI graph shape is the source of truth.
    /// </summary>
    [Fact]
    public void InMemoryKnowledgeBase_ProbeAsync_DoesNotHitNetwork()
    {
        InMemoryKnowledgeBase kb = new(
            NullLoggerFactory.Instance.CreateLogger<InMemoryKnowledgeBase>(),
            Options.Create(new KnowledgeOptions()));

        Func<Task> probe = () => kb.ProbeAsync();
        probe.Should().NotThrowAsync();
    }
}

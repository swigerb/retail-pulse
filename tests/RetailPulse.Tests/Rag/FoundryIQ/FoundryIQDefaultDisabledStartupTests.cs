using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Rag;
using RetailPulse.Api.Rag.FoundryIQ;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Tests.Rag.FoundryIQ;

/// <summary>
/// Optional-default proof for issue #104. Mirrors the Program.cs wiring for
/// the Knowledge section without any Foundry configuration, then verifies:
///
/// 1. The provider selector resolves <see cref="KnowledgeProviderMode.InMemory"/>.
/// 2. The provider registry contains ONLY the InMemory factory (Foundry IQ NOT registered).
/// 3. No Foundry-specific DI type is materialized — no options, no accessor, no KB.
/// 4. Selecting <c>Mode=FoundryIQ</c> without wiring the provider fails startup
///    with the shared unregistered-mode message (never a silent no-op).
/// </summary>
public sealed class FoundryIQDefaultDisabledStartupTests
{
    [Fact]
    public void DefaultConfiguration_ResolvesInMemoryAndOmitsFoundryIQ()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        using ServiceProvider sp = BuildServiceProvider(config);

        KnowledgeProviderSelector selector = sp.GetRequiredService<KnowledgeProviderSelector>();
        selector.ResolveMode().Should().Be(KnowledgeProviderMode.InMemory);
        selector.ResolveDegradation().Should().Be(KnowledgeDegradationMode.FailLoud);

        KnowledgeProviderRegistry registry = sp.GetRequiredService<KnowledgeProviderRegistry>();
        registry.RegisteredModes.Should().ContainSingle().Which.Should().Be(KnowledgeProviderMode.InMemory);
        registry.IsRegistered(KnowledgeProviderMode.FoundryIQ).Should().BeFalse(
            "default demo config MUST NOT register a Foundry IQ factory");

        sp.GetService<FoundryIQKnowledgeBase>().Should().BeNull(
            "the KB itself must not be resolvable when the endpoint is blank");
        sp.GetService<FoundryIQOptions>().Should().BeNull(
            "the strongly-typed options singleton is only registered on the enabled path");
        sp.GetService<FoundryClientAccessor>().Should().BeNull(
            "no PersistentAgentsClient accessor may be materialised on the disabled path");

        // Materialize the primary and probe — no exception, no external call.
        IKnowledgeBase primary = selector.CreatePrimary(sp);
        primary.GetCapabilities().ProviderName.Should().Be(InMemoryKnowledgeBase.ProviderName);
        Func<Task> probe = () => primary.ProbeAsync();
        probe.Should().NotThrowAsync();
    }

    [Fact]
    public void DefaultConfiguration_SelectingFoundryIQ_FailsLoudlyWithActionableMessage()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Mode selected but the provider extension was never wired.
                ["Knowledge:Provider:Mode"] = "FoundryIQ",
            })
            .Build();

        using ServiceProvider sp = BuildServiceProvider(config);

        KnowledgeProviderSelector selector = sp.GetRequiredService<KnowledgeProviderSelector>();
        Action act = () => selector.CreatePrimary(sp);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not registered*")
            .WithMessage("*FoundryIQ*");
    }

    [Fact]
    public void PartialConfiguration_EndpointOnly_StaysNoOp()
    {
        // Endpoint alone without a vector store binding is a partial config;
        // it MUST stay a no-op so misconfigured setups can never contact
        // Foundry with an incomplete binding.
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Knowledge:FoundryIQ:ProjectEndpoint"] = "https://foundry.example/api/projects/p",
            })
            .Build();

        using ServiceProvider sp = BuildServiceProvider(config);

        sp.GetRequiredService<KnowledgeProviderRegistry>()
            .IsRegistered(KnowledgeProviderMode.FoundryIQ).Should().BeFalse();
        sp.GetService<FoundryIQKnowledgeBase>().Should().BeNull();
    }

    private static ServiceProvider BuildServiceProvider(IConfiguration config)
    {
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

        // Target line under test — must be a no-op on blank/partial config.
        services.AddFoundryIQKnowledgeProvider(config);

        return services.BuildServiceProvider();
    }
}

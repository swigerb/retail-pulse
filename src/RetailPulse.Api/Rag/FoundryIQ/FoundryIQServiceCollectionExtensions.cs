using Azure.AI.Agents.Persistent;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Api.Rag.FoundryIQ;

/// <summary>
/// Opt-in registration of the Foundry IQ (file_search) knowledge provider
/// (issue #104).
///
/// The provider is FULLY OPTIONAL. When <c>Knowledge:FoundryIQ:ProjectEndpoint</c>
/// is blank (or no vector-store selector is set), this extension is a no-op:
/// no <see cref="PersistentAgentsClient"/>, no <see cref="Azure.AI.Projects.AIProjectClient"/>,
/// no <see cref="TokenCredential"/>, no <see cref="FoundryClientAccessor"/>,
/// no <see cref="FoundryIQKnowledgeBase"/>, and no
/// <see cref="IKnowledgeProviderContribution"/> is materialized. The
/// <see cref="KnowledgeProviderRegistry"/> stays InMemory-only. Selecting
/// <see cref="KnowledgeProviderMode.FoundryIQ"/> without configuring the
/// endpoint fails startup with the shared unregistered-mode message from
/// <see cref="KnowledgeProviderRegistry"/>. See ADR-013.
/// </summary>
public static class FoundryIQServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Foundry IQ knowledge provider (idempotent).
    ///
    /// Behaviour by configuration state:
    /// <list type="bullet">
    ///   <item><description>Blank endpoint or missing vector-store selector — no
    ///   registrations added. Default demo path unchanged.</description></item>
    ///   <item><description>Configured — registers <see cref="FoundryIQKnowledgeBase"/>
    ///   and its supporting resolver/agent-provider, and adds the factory to
    ///   <see cref="KnowledgeProviderRegistry"/> so
    ///   <c>Mode=FoundryIQ</c> resolves it.</description></item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddFoundryIQKnowledgeProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new FoundryIQOptions();
        configuration.GetSection(FoundryIQOptions.SectionName).Bind(options);
        if (!options.IsConfigured)
        {
            // Fully-optional gate. The registration is a no-op so selecting
            // Knowledge:Provider:Mode=FoundryIQ without wiring the provider
            // fails loudly via KnowledgeProviderRegistry.Create — never a
            // silent degradation.
            return services;
        }

        options.ValidateEnabled();

        // Bind the strongly-typed options for consumers and register a
        // singleton copy for direct-inject sites (resolvers, tests).
        services.Configure<FoundryIQOptions>(
            configuration.GetSection(FoundryIQOptions.SectionName));
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<FoundryIQOptions>>().Value);

        // One credential per process, shared with the AzureAISearch provider
        // when both are configured (TryAddSingleton — first-wins).
        services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential());

        // Per-endpoint PersistentAgentsClient accessor owned by this provider.
        // Currently the accessor only sees the client it lazily constructs
        // itself via GetOrCreate — AgentServiceExtensions.AddAzureAgent<TAgent>
        // (the FoundryAgent shipment path) constructs its own
        // PersistentAgentsClient directly and does not register one in DI or
        // in this accessor. The two optional features are independently gated
        // and can target the same Foundry project, but currently construct
        // independent SDK clients. The accessor's Register seam is retained as
        // the forward-compatible entry point for a future cross-surface
        // sharing effort. See ADR-013 (Relationship with FoundryAgent:*).
        services.TryAddSingleton(sp =>
            new FoundryClientAccessor(sp.GetRequiredService<TokenCredential>()));

        services.TryAddSingleton(sp =>
        {
            FoundryIQOptions opts = sp.GetRequiredService<FoundryIQOptions>();
            FoundryClientAccessor accessor = sp.GetRequiredService<FoundryClientAccessor>();
            return accessor.GetOrCreate(opts.ProjectEndpoint!);
        });

        services.TryAddSingleton<IFoundryIQClient>(sp => new FoundryIQClient(
            sp.GetRequiredService<PersistentAgentsClient>(),
            sp.GetRequiredService<FoundryIQOptions>()));

        services.TryAddSingleton<FoundryIQVectorStoreResolver>();
        services.TryAddSingleton<FoundryIQRetrievalAgentProvider>();

        services.TryAddSingleton(sp => new FoundryIQKnowledgeBase(
            sp.GetRequiredService<IFoundryIQClient>(),
            sp.GetRequiredService<FoundryIQVectorStoreResolver>(),
            sp.GetRequiredService<FoundryIQRetrievalAgentProvider>(),
            sp.GetRequiredService<FoundryIQOptions>(),
            sp.GetRequiredService<IOptions<KnowledgeOptions>>().Value,
            sp.GetRequiredService<ICostTracker>(),
            sp.GetRequiredService<ILogger<FoundryIQKnowledgeBase>>()));

        services.AddSingleton<IKnowledgeProviderContribution, FoundryIQProviderContribution>();

        return services;
    }
}

/// <summary>
/// Contributes the Foundry IQ provider factory to the shared
/// <see cref="KnowledgeProviderRegistry"/>. Consumed by the registry singleton
/// factory in <c>Program.cs</c>.
/// </summary>
internal sealed class FoundryIQProviderContribution : IKnowledgeProviderContribution
{
    public void Register(KnowledgeProviderRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(
            KnowledgeProviderMode.FoundryIQ,
            sp => sp.GetRequiredService<FoundryIQKnowledgeBase>());
    }
}

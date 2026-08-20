using RetailPulse.Contracts.Rag;

namespace RetailPulse.Api.Rag;

/// <summary>
/// Registry of <see cref="IKnowledgeBase"/> provider factories keyed by
/// <see cref="KnowledgeProviderMode"/>. Provides the reusable seam future
/// cloud-provider issues (#103, #104) and tests plug into without touching
/// the selection or degradation code.
///
/// The registry is populated during DI configuration. The InMemory factory
/// is registered by default. Cloud providers register themselves from their
/// respective opt-in modules; tests register deliberately unreachable stubs
/// to exercise the degradation policy.
/// </summary>
public sealed class KnowledgeProviderRegistry
{
    private readonly Dictionary<KnowledgeProviderMode, Func<IServiceProvider, IKnowledgeBase>> _factories = [];

    /// <summary>
    /// Registers (or replaces) the factory that materializes the provider for
    /// <paramref name="mode"/>. The factory receives the request-scoped
    /// <see cref="IServiceProvider"/> so it can pull its own dependencies (e.g.
    /// options, loggers, HttpClient) from DI.
    /// </summary>
    public void Register(KnowledgeProviderMode mode, Func<IServiceProvider, IKnowledgeBase> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factories[mode] = factory;
    }

    /// <summary>
    /// True when a factory has been registered for <paramref name="mode"/>.
    /// </summary>
    public bool IsRegistered(KnowledgeProviderMode mode) => _factories.ContainsKey(mode);

    /// <summary>
    /// Modes that currently have a registered factory. Useful for producing
    /// actionable error messages when the selected mode is not available.
    /// </summary>
    public IReadOnlyCollection<KnowledgeProviderMode> RegisteredModes => _factories.Keys;

    /// <summary>
    /// Resolves the provider for <paramref name="mode"/> using the registered
    /// factory. Throws with a clear message when the mode has not been
    /// registered — this happens when a cloud mode is selected without the
    /// corresponding opt-in module wired up.
    /// </summary>
    public IKnowledgeBase Create(KnowledgeProviderMode mode, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!_factories.TryGetValue(mode, out Func<IServiceProvider, IKnowledgeBase>? factory))
        {
            string registered = _factories.Count == 0
                ? "(none)"
                : string.Join(", ", _factories.Keys.Select(m => m.ToString()));
            throw new InvalidOperationException(
                $"Knowledge provider mode '{mode}' is not registered. " +
                $"Registered modes: {registered}. " +
                "Register the provider factory via KnowledgeProviderRegistry.Register(...) " +
                "before starting the host, or change Knowledge:Provider:Mode to a registered value.");
        }

        return factory(services);
    }
}

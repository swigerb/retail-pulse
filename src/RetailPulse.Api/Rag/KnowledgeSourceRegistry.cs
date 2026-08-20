using System.Collections.Frozen;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Models;

namespace RetailPulse.Api.Rag;

/// <summary>
/// Startup-resolved catalog of per-agent knowledge bindings for issue #105.
///
/// The registry combines:
/// <list type="bullet">
///   <item>The named knowledge sources declared in
///     <see cref="KnowledgeSourcesOptions"/> (config).</item>
///   <item>The per-agent flags declared on
///     <see cref="AgentDefinition.UseKnowledgeBase"/> and
///     <see cref="AgentDefinition.KnowledgeBaseName"/> (prompts.yaml).</item>
/// </list>
///
/// Every agent reference to a named source is resolved once at startup; an
/// unknown name aborts startup with a message that includes the agent key,
/// the unknown name, and the set of valid names. Sharing is allowed — several
/// agents may reference the same name.
///
/// The registry never inspects the underlying corpus for the presence of the
/// declared documents. A freshly deployed cloud index that has not been
/// seeded yet must still start; validation is purely against configuration
/// names.
/// </summary>
public sealed class KnowledgeSourceRegistry
{
    private readonly FrozenDictionary<string, KnowledgeBinding> _bindings;

    /// <summary>
    /// Fallback binding used when an agent key is unknown to the registry
    /// (e.g., orchestration prompts, tests). Preserves the pre-#105 default of
    /// unscoped, always-enabled retrieval.
    /// </summary>
    public static KnowledgeBinding Default { get; } = new(Enabled: true, Sources: []);

    private KnowledgeSourceRegistry(FrozenDictionary<string, KnowledgeBinding> bindings)
    {
        _bindings = bindings;
    }

    /// <summary>
    /// Builds the registry from configuration and the composed prompt
    /// definitions. Throws <see cref="InvalidOperationException"/> when any
    /// agent references an unregistered named source.
    /// </summary>
    public static KnowledgeSourceRegistry Build(
        KnowledgeSourcesOptions sourcesOptions,
        IReadOnlyDictionary<string, AgentDefinition> agents)
    {
        ArgumentNullException.ThrowIfNull(sourcesOptions);
        ArgumentNullException.ThrowIfNull(agents);

        // Materialize the named-source table (case-insensitive) and prune any
        // definition with no documents so the effective set is honest — an
        // agent binding to an empty-source name would return zero hits, which
        // is indistinguishable from an unknown reference and confuses ops.
        var namedTable = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, KnowledgeSourceDefinition def) in sourcesOptions.Named)
        {
            var docs = def.Documents
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (docs.Length == 0)
            {
                continue;
            }

            namedTable[name.Trim()] = docs;
        }

        var bindings = new Dictionary<string, KnowledgeBinding>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, AgentDefinition def) in agents)
        {
            string agentKey = string.IsNullOrWhiteSpace(def.Key) ? key : def.Key;

            if (!def.UseKnowledgeBase)
            {
                bindings[agentKey] = new KnowledgeBinding(Enabled: false, Sources: []);
                continue;
            }

            if (string.IsNullOrWhiteSpace(def.KnowledgeBaseName))
            {
                // Enabled + unscoped — every ingested document is eligible.
                bindings[agentKey] = new KnowledgeBinding(Enabled: true, Sources: []);
                continue;
            }

            string requested = def.KnowledgeBaseName.Trim();
            if (!namedTable.TryGetValue(requested, out IReadOnlyList<string>? docs))
            {
                string valid = namedTable.Count == 0
                    ? "<none configured>"
                    : string.Join(", ", namedTable.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
                throw new InvalidOperationException(
                    $"Agent '{agentKey}' references unknown knowledge source '{requested}'. " +
                    $"Configure it under {KnowledgeSourcesOptions.SectionName}:Named or fix the " +
                    $"'knowledge_base_name' in prompts.yaml. Valid names: {valid}.");
            }

            bindings[agentKey] = new KnowledgeBinding(Enabled: true, Sources: docs);
        }

        return new KnowledgeSourceRegistry(bindings.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves the binding for the given agent key. Falls back to
    /// <see cref="Default"/> for unknown keys so orchestration prompts and
    /// test doubles keep their pre-#105 semantics.
    /// </summary>
    public KnowledgeBinding GetBinding(string agentKey) =>
        string.IsNullOrWhiteSpace(agentKey)
            ? Default
            : _bindings.TryGetValue(agentKey, out KnowledgeBinding binding) ? binding : Default;

    /// <summary>All configured named sources — used by tests and diagnostics.</summary>
    public IReadOnlyDictionary<string, KnowledgeBinding> Bindings => _bindings;
}

/// <summary>
/// Resolved per-agent knowledge-binding record. <see cref="Enabled"/> mirrors
/// <see cref="AgentDefinition.UseKnowledgeBase"/>; <see cref="Sources"/> holds
/// the resolved provider-side <c>source</c> field values (empty list means
/// unscoped).
/// </summary>
public readonly record struct KnowledgeBinding(bool Enabled, IReadOnlyList<string> Sources)
{
    public bool IsScoped => Enabled && Sources.Count > 0;
}

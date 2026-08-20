using Microsoft.Extensions.AI;

namespace RetailPulse.Api.Agents.Tools;

/// <summary>
/// Named registry of <see cref="AITool"/> factories. Each specialist
/// <c>AgentDefinition</c> lists the tools it needs by name (matching
/// <c>[Description]</c>-annotated MCP proxy method names in
/// <c>prompts.yaml</c>). The registry converts a list of names into
/// concrete <see cref="AITool"/> instances at agent construction time.
/// </summary>
/// <remarks>
/// Unknown tool names must be caught at startup, not at first user query.
/// Callers use <see cref="Resolve"/> which throws
/// <see cref="UnknownToolReferenceException"/> when any name is missing, so
/// composition-root wiring fails loudly before the app accepts traffic.
/// </remarks>
public sealed class AgentToolRegistry
{
    private readonly Dictionary<string, Func<IServiceProvider, AITool>> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registered tool names (case-insensitive keys).</summary>
    public IReadOnlyCollection<string> RegisteredNames => _factories.Keys;

    /// <summary>
    /// Register a factory for a named tool. The factory is invoked lazily
    /// per agent construction so scoped tools resolve against the correct
    /// <see cref="IServiceProvider"/>.
    /// </summary>
    /// <param name="name">Tool name as it appears in <c>prompts.yaml</c>.</param>
    /// <param name="factory">Factory that produces the <see cref="AITool"/>.</param>
    public AgentToolRegistry Register(string name, Func<IServiceProvider, AITool> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);

        return !_factories.TryAdd(name, factory)
            ? throw new InvalidOperationException(
                $"Tool '{name}' is already registered in {nameof(AgentToolRegistry)}. " +
                "Each tool name must be unique — this usually indicates duplicate DI wiring.")
            : this;
    }

    /// <summary>True when a tool of the given name is registered.</summary>
    public bool Contains(string name) => _factories.ContainsKey(name);

    /// <summary>
    /// Resolves the requested tool names into concrete <see cref="AITool"/> instances
    /// via the supplied <paramref name="sp"/>. Throws
    /// <see cref="UnknownToolReferenceException"/> when any name is missing so callers
    /// can surface an actionable startup error and never register an incomplete agent.
    /// </summary>
    public IReadOnlyList<AITool> Resolve(IEnumerable<string> toolNames, IServiceProvider sp)
    {
        ArgumentNullException.ThrowIfNull(toolNames);
        ArgumentNullException.ThrowIfNull(sp);

        IList<string> names = toolNames as IList<string> ?? [.. toolNames];
        var missing = new List<string>();
        var resolved = new List<AITool>(names.Count);

        foreach (string rawName in names)
        {
            string name = rawName?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(name))
                continue;

            if (_factories.TryGetValue(name, out Func<IServiceProvider, AITool>? factory))
            {
                resolved.Add(factory(sp));
            }
            else
            {
                missing.Add(name);
            }
        }

        return missing.Count > 0 ? throw new UnknownToolReferenceException(missing, _factories.Keys) : (IReadOnlyList<AITool>)resolved;
    }

    /// <summary>
    /// Validates that every tool name referenced by any <paramref name="agents"/> definition
    /// has a matching factory. Throws <see cref="UnknownToolReferenceException"/> otherwise.
    /// Intended to be called once at startup — before any specialist is registered — so a
    /// typo in prompts.yaml never leaks into a running deployment.
    /// </summary>
    public void ValidateAllReferences(IEnumerable<AgentDefinitionRef> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);

        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AgentDefinitionRef def in agents)
        {
            foreach (string name in def.Tools)
            {
                if (!string.IsNullOrWhiteSpace(name) && !_factories.ContainsKey(name.Trim()))
                {
                    missing.Add(name.Trim());
                }
            }
        }

        if (missing.Count > 0)
        {
            throw new UnknownToolReferenceException([.. missing], _factories.Keys);
        }
    }
}

/// <summary>
/// Minimal projection of an agent definition used for cross-cutting startup validation.
/// Kept as a small record so <see cref="AgentToolRegistry"/> does not take a hard
/// dependency on <c>RetailPulse.Api.Models.AgentDefinition</c>.
/// </summary>
public sealed record AgentDefinitionRef(string Key, IReadOnlyList<string> Tools);

/// <summary>
/// Thrown at startup when an agent definition references a tool name that has no
/// registered factory. Carries the full list of missing and known names so the
/// operator can pick the closest match without digging through source.
/// </summary>
public sealed class UnknownToolReferenceException : InvalidOperationException
{
    public IReadOnlyList<string> MissingTools { get; }
    public IReadOnlyList<string> KnownTools { get; }

    public UnknownToolReferenceException(
        IReadOnlyList<string> missing,
        IEnumerable<string> known)
        : base(BuildMessage(missing, known))
    {
        MissingTools = missing;
        KnownTools = [.. known.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
    }

    private static string BuildMessage(IReadOnlyList<string> missing, IEnumerable<string> known)
    {
        string knownList = string.Join(", ", known.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        return $"Agent definition references unknown tool(s): {string.Join(", ", missing)}. " +
               $"Registered tools: {knownList}. " +
               "Fix the 'tools:' entry in prompts.yaml or register the missing tool factory before " +
               "AddAgentRouting(). Configuration errors must fail at startup, not at first user query.";
    }
}

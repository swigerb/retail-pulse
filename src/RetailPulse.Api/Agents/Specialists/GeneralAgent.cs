using Microsoft.Extensions.AI;
using RetailPulse.Api.Models;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Agents.Specialists;

/// <summary>
/// The General specialist — handles unclassified and general/fallback queries.
/// The router sends anything it can't classify here.
/// </summary>
/// <remarks>
/// Thin shim over <see cref="ConfiguredSpecialistAgent"/> retained so DI-keyed
/// callers (composition root, tests) and the legacy <c>RetailPulseAgent</c>
/// facade can reference a stable type. All behavior lives in the base class —
/// per issue #98's "single specialist implementation" objective.
/// </remarks>
public sealed class GeneralAgent : ConfiguredSpecialistAgent
{
    public GeneralAgent(
        IAgentExecutionPipeline pipeline,
        AgentDefinition agentDef,
        IEnumerable<AITool> tools)
        : base(pipeline, EnsureDefaults(agentDef), tools)
    {
    }

    private static AgentDefinition EnsureDefaults(AgentDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);

        def = def.Clone();

        // The old class hardcoded Key="general" / General intent. Preserve that
        // behavior for callers that construct with a minimally-populated definition
        // (tests and legacy code paths) while still honoring an explicit Key/Intents
        // supplied via prompts.yaml when they are present.
        if (string.IsNullOrWhiteSpace(def.Key))
            def.Key = "general";
        if (def.Intents.Count == 0)
            def.Intents = [AgentIntent.General];
        if (string.IsNullOrWhiteSpace(def.DisplayName))
            def.DisplayName = "General Agent";
        return def;
    }
}

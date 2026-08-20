using Microsoft.Extensions.AI;
using RetailPulse.Api.Models;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Agents.Specialists;

/// <summary>
/// Planogram Optimization specialist — thin shim over <see cref="ConfiguredSpecialistAgent"/>.
/// </summary>
public sealed class PlanogramAgent : ConfiguredSpecialistAgent
{
    public PlanogramAgent(
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

        if (string.IsNullOrWhiteSpace(def.Key))
            def.Key = "planogram";
        if (def.Intents.Count == 0)
            def.Intents = [AgentIntent.Planogram];
        if (string.IsNullOrWhiteSpace(def.DisplayName))
            def.DisplayName = "Planogram Optimization Agent";
        if (string.IsNullOrWhiteSpace(def.FallbackReply))
            def.FallbackReply = "I wasn't able to generate a planogram optimization response.";
        return def;
    }
}

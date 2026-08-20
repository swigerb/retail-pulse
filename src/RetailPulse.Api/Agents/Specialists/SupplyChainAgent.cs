using Microsoft.Extensions.AI;
using RetailPulse.Api.Models;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Agents.Specialists;

/// <summary>
/// Supply Chain specialist — thin shim over <see cref="ConfiguredSpecialistAgent"/>.
/// </summary>
public sealed class SupplyChainAgent : ConfiguredSpecialistAgent
{
    public SupplyChainAgent(
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
            def.Key = "supply-chain";
        if (def.Intents.Count == 0)
            def.Intents = [AgentIntent.SupplyShipments];
        if (string.IsNullOrWhiteSpace(def.DisplayName))
            def.DisplayName = "Supply Chain Agent";
        if (string.IsNullOrWhiteSpace(def.FallbackReply))
            def.FallbackReply = "I wasn't able to generate a supply chain analysis.";
        return def;
    }
}

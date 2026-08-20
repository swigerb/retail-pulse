using Microsoft.Extensions.AI;
using RetailPulse.Api.Models;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Agents.Specialists;

/// <summary>
/// Margin Analysis specialist — thin shim over <see cref="ConfiguredSpecialistAgent"/>.
/// </summary>
public sealed class MarginAgent : ConfiguredSpecialistAgent
{
    public MarginAgent(
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
            def.Key = "margin-analysis";
        if (def.Intents.Count == 0)
            def.Intents = [AgentIntent.MarginAnalysis];
        if (string.IsNullOrWhiteSpace(def.DisplayName))
            def.DisplayName = "Margin Analysis Agent";
        if (string.IsNullOrWhiteSpace(def.FallbackReply))
            def.FallbackReply = "I wasn't able to generate a margin analysis response.";
        return def;
    }
}

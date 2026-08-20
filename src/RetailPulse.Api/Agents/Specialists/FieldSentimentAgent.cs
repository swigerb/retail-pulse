using Microsoft.Extensions.AI;
using RetailPulse.Api.Models;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Agents.Specialists;

/// <summary>
/// Field Sentiment specialist — thin shim over
/// <see cref="ConfiguredSpecialistAgent"/>. Behavior is derived entirely from
/// its <see cref="AgentDefinition"/>.
/// </summary>
public sealed class FieldSentimentAgent : ConfiguredSpecialistAgent
{
    public FieldSentimentAgent(
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
            def.Key = "field-sentiment";
        if (def.Intents.Count == 0)
            def.Intents = [AgentIntent.SentimentField];
        if (string.IsNullOrWhiteSpace(def.DisplayName))
            def.DisplayName = "Field Sentiment Agent";
        if (string.IsNullOrWhiteSpace(def.FallbackReply))
            def.FallbackReply = "I wasn't able to retrieve field sentiment data.";
        return def;
    }
}

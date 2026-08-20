using Microsoft.Extensions.AI;
using RetailPulse.Api.Models;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Agents.Specialists;

/// <summary>
/// Demand Forecasting specialist — thin shim over
/// <see cref="ConfiguredSpecialistAgent"/> retained so DI, the predictive
/// prefetch service, and tests can reference a stable named type.
/// </summary>
public sealed class DemandForecastAgent : ConfiguredSpecialistAgent
{
    public DemandForecastAgent(
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
            def.Key = "demand-forecasting";
        if (def.Intents.Count == 0)
            def.Intents = [AgentIntent.DemandForecasting];
        if (string.IsNullOrWhiteSpace(def.DisplayName))
            def.DisplayName = "Demand Forecast Agent";
        if (string.IsNullOrWhiteSpace(def.FallbackReply))
            def.FallbackReply = "I wasn't able to generate a forecast response.";
        def.Prefetchable = true;
        return def;
    }
}

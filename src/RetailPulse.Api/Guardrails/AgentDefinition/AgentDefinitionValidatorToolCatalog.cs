using Microsoft.Extensions.AI;
using RetailPulse.Api.Agents.Tools;

namespace RetailPulse.Api.Guardrails.AgentDefinition;

/// <summary>
/// Composition-root helper that produces the read-only catalog of tool names
/// used by <see cref="AgentDefinitionValidator"/> to enforce the
/// deployment-permitted tool set at load time. Every name registered here
/// mirrors the runtime <c>AgentToolRegistry</c> registration in Program.cs so
/// the validator's "unknown tool" check is aligned with what the pipeline can
/// actually resolve at request time.
/// </summary>
/// <remarks>
/// The catalog registers name-only stub factories that never execute — the
/// validator only queries <see cref="AgentToolRegistry.RegisteredNames"/> and
/// <see cref="AgentToolRegistry.Contains(string)"/>. This lets the load-time
/// validation run without instantiating real tools (which depend on scoped
/// services that only exist after the full DI graph is built), while keeping
/// the tool name authority in one place.
/// </remarks>
internal static class AgentDefinitionValidatorToolCatalog
{
    /// <summary>
    /// Canonical tool names accepted by the runtime <c>AgentToolRegistry</c>.
    /// Adding a tool name to Program.cs also requires an entry here; the
    /// <c>AgentDefinitionValidatorToolCatalogTests</c> assertion pins the two
    /// lists together.
    /// </summary>
    public static IReadOnlyList<string> KnownToolNames { get; } =
    [
        "GetDepletionStats",
        "GetPortfolioDepletionStats",
        "GetFieldSentiment",
        "GetShipmentStats",
        "GetVariantMix",
        "CreateChart",
        "AnalyzeShipments",
        "GetHistoricalDemand",
        "GenerateForecast",
        "GetSeasonalityFactors",
        "IdentifyDemandRisks",
        "GetPromoHistory",
        "CalculateLift",
        "EvaluateTiming",
        "EstimateROI",
        "RequestApproval",
        "GetCompetitorPricing",
        "GetMarketShare",
        "DetectThreats",
        "GetCompetitiveLandscape",
        "GetInventoryLevels",
        "GetSupplyDisruptions",
        "GetFulfillmentRate",
        "GetSupplyHealthSummary",
        "GetStorePerformance",
        "GetShelfLayout",
        "OptimizePlanogram",
        "PredictStockout",
        "GetMarginByBrand",
        "GetMarginDrivers",
        "GetMarginTrend",
        "DetectMarginRisks",
    ];

    public static AgentToolRegistry Build()
    {
        var registry = new AgentToolRegistry();
        foreach (string name in KnownToolNames)
        {
            registry.Register(name, StubFactory);
        }
        return registry;
    }

    private static AITool StubFactory(IServiceProvider _) =>
        throw new InvalidOperationException(
            "AgentDefinitionValidatorToolCatalog stubs are name-only — they must never be resolved at runtime.");
}

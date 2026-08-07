using System.ComponentModel;
using System.Reflection;
using FluentAssertions;
using RetailPulse.Api.Tools;
using Xunit;

namespace RetailPulse.Tests.Budget;

/// <summary>
/// Catalog contract gate. Enumerates every model-callable agent tool in the
/// RetailPulse.Api assembly (public methods decorated with <see cref="DescriptionAttribute"/>
/// returning a JSON string, hosted by a type in a <c>.Tools</c> namespace) and asserts
/// each one has an explicit, bounded-output classification.
///
/// Because the budget boundary is wired at the single AgentExecutionPipeline tool-wrap
/// choke point, every non-exempt tool is bounded by construction. This test forces that
/// coverage to be acknowledged: adding a new agent tool without classifying it here fails
/// CI, so an unbounded tool can never silently enter model context.
/// </summary>
public sealed class ToolCatalogContractTests
{
    /// <summary>How a tool's output is kept within the model-context budget.</summary>
    private enum BoundingStrategy
    {
        /// <summary>Carries a canonical payload the frontend needs — never compacted (e.g. CreateChart).</summary>
        Exempt,

        /// <summary>Has a dedicated <see cref="Api.Budget.IToolResultCompactor"/> projection.</summary>
        ToolSpecificSummarizer,

        /// <summary>Bounded by the generic array-truncation + hard-clip fallback and per-request caps.</summary>
        GenericBudget
    }

    /// <summary>
    /// Authoritative classification of every registered agent tool. Adding a new tool
    /// requires adding it here (see the discovery test below), which is the CI gate.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, BoundingStrategy> Classification =
        new Dictionary<string, BoundingStrategy>(StringComparer.Ordinal)
        {
            // Canonical / exempt.
            ["CreateChart"] = BoundingStrategy.Exempt,

            // Dedicated summarizers (largest / structurally-verbose payloads).
            ["GetHistoricalDemand"] = BoundingStrategy.ToolSpecificSummarizer,
            ["GetPortfolioDepletionStats"] = BoundingStrategy.ToolSpecificSummarizer,

            // Bounded by the generic budget fallback + per-request caps.
            ["GetDepletionStats"] = BoundingStrategy.GenericBudget,
            ["GetFieldSentiment"] = BoundingStrategy.GenericBudget,
            ["GetShipmentStats"] = BoundingStrategy.GenericBudget,
            ["GetVariantMix"] = BoundingStrategy.GenericBudget,
            ["AnalyzeShipments"] = BoundingStrategy.GenericBudget,
            ["GenerateForecast"] = BoundingStrategy.GenericBudget,
            ["GetSeasonalityFactors"] = BoundingStrategy.GenericBudget,
            ["IdentifyDemandRisks"] = BoundingStrategy.GenericBudget,
            ["RequestApproval"] = BoundingStrategy.GenericBudget,
            ["GetPromoHistory"] = BoundingStrategy.GenericBudget,
            ["CalculateLift"] = BoundingStrategy.GenericBudget,
            ["EvaluateTiming"] = BoundingStrategy.GenericBudget,
            ["EstimateROI"] = BoundingStrategy.GenericBudget,
            ["GetCompetitorPricing"] = BoundingStrategy.GenericBudget,
            ["GetMarketShare"] = BoundingStrategy.GenericBudget,
            ["DetectThreats"] = BoundingStrategy.GenericBudget,
            ["GetCompetitiveLandscape"] = BoundingStrategy.GenericBudget,
            ["GetInventoryLevels"] = BoundingStrategy.GenericBudget,
            ["GetSupplyDisruptions"] = BoundingStrategy.GenericBudget,
            ["GetFulfillmentRate"] = BoundingStrategy.GenericBudget,
            ["GetSupplyHealthSummary"] = BoundingStrategy.GenericBudget,
            ["GetStorePerformance"] = BoundingStrategy.GenericBudget,
            ["GetShelfLayout"] = BoundingStrategy.GenericBudget,
            ["OptimizePlanogram"] = BoundingStrategy.GenericBudget,
            ["PredictStockout"] = BoundingStrategy.GenericBudget,
            ["GetMarginByBrand"] = BoundingStrategy.GenericBudget,
            ["GetMarginDrivers"] = BoundingStrategy.GenericBudget,
            ["GetMarginTrend"] = BoundingStrategy.GenericBudget,
            ["DetectMarginRisks"] = BoundingStrategy.GenericBudget
        };

    /// <summary>
    /// Reflect over the API assembly for model-callable tool methods: public instance
    /// methods with a <see cref="DescriptionAttribute"/> returning <c>string</c> or
    /// <c>Task&lt;string&gt;</c>, hosted by a type whose namespace contains ".Tools".
    /// </summary>
    private static IReadOnlyList<string> DiscoverToolMethodNames()
    {
        Assembly api = typeof(ChartDataTool).Assembly;
        var names = new SortedSet<string>(StringComparer.Ordinal);

        foreach (Type type in api.GetTypes())
        {
            if (type.Namespace is null || !type.Namespace.Contains(".Tools", StringComparison.Ordinal))
                continue;
            if (type.IsAbstract || type.IsInterface)
                continue;

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.GetCustomAttribute<DescriptionAttribute>() is null)
                    continue;

                Type ret = method.ReturnType;
                bool returnsJsonString =
                    ret == typeof(string) ||
                    ret == typeof(Task<string>) ||
                    ret == typeof(ValueTask<string>);
                if (!returnsJsonString)
                    continue;

                names.Add(method.Name);
            }
        }

        return [.. names];
    }

    [Fact]
    public void EveryRegisteredAgentTool_HasABoundedOutputClassification()
    {
        IReadOnlyList<string> discovered = DiscoverToolMethodNames();

        discovered.Should().NotBeEmpty("the reflection scan must find the agent tool catalog");

        // Any discovered tool missing from the classification registry fails the gate.
        // This is what breaks CI when someone adds a new (potentially unbounded) tool
        // without declaring how its output is kept within the model-context budget.
        IEnumerable<string> unclassified = discovered.Where(n => !Classification.ContainsKey(n));
        unclassified.Should().BeEmpty(
            "every agent tool must declare a bounded-output strategy in ToolCatalogContractTests.Classification; " +
            "add the new tool with Exempt, ToolSpecificSummarizer, or GenericBudget (and a summarizer if its output is large/unbounded)");
    }

    [Fact]
    public void Classification_HasNoStaleEntries()
    {
        // Keep the registry honest: every classified name must still exist as a real tool.
        IReadOnlyList<string> discovered = DiscoverToolMethodNames();
        IEnumerable<string> stale = Classification.Keys.Where(k => !discovered.Contains(k));
        stale.Should().BeEmpty("remove classifications for tools that no longer exist");
    }

    [Fact]
    public void SummarizerClassifiedTools_HaveAMatchingCompactor()
    {
        var compactors = new Api.Budget.IToolResultCompactor[]
        {
            new Api.Budget.HistoricalDemandCompactor(),
            new Api.Budget.PortfolioDepletionCompactor()
        };

        foreach ((string tool, BoundingStrategy strategy) in Classification)
        {
            if (strategy != BoundingStrategy.ToolSpecificSummarizer)
                continue;

            compactors.Any(c => c.CanCompact(tool)).Should().BeTrue(
                $"tool '{tool}' is classified ToolSpecificSummarizer but no IToolResultCompactor handles it");
        }
    }
}

using RetailPulse.Contracts.Routing;

namespace RetailPulse.Contracts.Charts;

/// <summary>
/// Data-source families a curated chart prompt draws from. Used by acceptance tests
/// to assert the tool/compactor layer emits a COMPLETE aggregate shape for the prompt,
/// and to document the expected fulfillment path per prompt.
/// </summary>
public enum ChartDataSource
{
    /// <summary>GetHistoricalDemand → compacted <c>by_region</c> rollups (trends, velocity, grouped-region).</summary>
    HistoricalDemand,

    /// <summary>GetPortfolioDepletionStats → per-brand <c>depletions_yoy</c> (portfolio ranking/comparison).</summary>
    PortfolioDepletion,

    /// <summary>GetMarketShare → per-brand <c>share_percent</c> (share breakdown; sums sensibly).</summary>
    MarketShare,

    /// <summary>GetVariantMix → per-variant <c>mix_percent</c> (variant mix; sums to ~100%).</summary>
    VariantMix,

    /// <summary>GetInventoryLevels → <c>status_breakdown</c> (inventory-health gauge, 0–100).</summary>
    InventoryLevels,

    /// <summary>GetDepletionStats per brand/region (single-brand or home-improvement table).</summary>
    DepletionStats,
}

/// <summary>
/// One acceptance contract for a curated chart prompt: the exact prompt text (kept in
/// sync with the single prompt source <c>src/RetailPulse.Web/src/constants/prompts.ts</c>
/// via a contract test) plus the semantics a rendered chart MUST satisfy — chart type,
/// routed specialist, minimum series/marks, required axis/legend labels, unit expectation,
/// and data source.
/// </summary>
/// <param name="Prompt">The verbatim curated prompt text (single source of truth).</param>
/// <param name="ChartType">Canonical <see cref="ChartSpec.Type"/> the prompt must yield.</param>
/// <param name="RoutedIntent">Specialist <see cref="AgentIntent"/> that must own this request (never the council).</param>
/// <param name="MinSeries">Minimum number of legend-bearing series in the ChartSpec.</param>
/// <param name="MinMarks">Minimum number of finite datapoints across all series (bars/points/sectors/rows).</param>
/// <param name="RequiredEntities">Entity labels (brands, variants, categories) that must appear as a legend or category.</param>
/// <param name="AxisUnit">Expected value unit/axis semantics (documentation + validation hint).</param>
/// <param name="DataSource">The tool/compactor family that must supply a complete aggregate.</param>
/// <param name="PercentAxis">True when Y values are percentages (share/growth/mix/gauge) — bounds and sum tolerances apply.</param>
public sealed record ChartAcceptanceCase(
    string Prompt,
    string ChartType,
    string RoutedIntent,
    int MinSeries,
    int MinMarks,
    IReadOnlyList<string> RequiredEntities,
    string AxisUnit,
    ChartDataSource DataSource,
    bool PercentAxis = false);

/// <summary>
/// Canonical acceptance manifest for every curated chart prompt. This is the authoritative
/// backend mirror of the "Charts" category in the single prompt source
/// (<c>src/RetailPulse.Web/src/constants/prompts.ts</c>) plus the previously-validated
/// two-brand comparison prompt. A contract test (<c>ChartAcceptanceManifestContractTests</c>)
/// fails CI if this list drifts from the prompt source or the README chart list, so the two
/// language surfaces stay synchronized without duplicating free-text arrays in code.
///
/// Backend acceptance tests iterate <see cref="Cases"/> to assert every prompt produces a
/// renderable ChartSpec with finite data and correct semantics; the frontend mirror
/// (<c>chartAcceptance.ts</c>) drives the real-Recharts render suite.
/// </summary>
public static class ChartAcceptanceManifest
{
    /// <summary>Regions available for the seeded tenant (used for grouped/table region expectations).</summary>
    public static readonly IReadOnlyList<string> SeededRegions =
    [
        "Northeast", "Southeast", "Midwest", "Southwest", "West Coast", "Pacific Northwest",
    ];

    /// <summary>
    /// The eight curated "Charts" prompts (in prompt-source order) plus the previously
    /// validated two-brand QSR comparison prompt.
    /// </summary>
    public static readonly IReadOnlyList<ChartAcceptanceCase> Cases =
    [
        new(
            Prompt: "Create a line chart showing Sierra Gold Tequila depletion trends across all regions",
            ChartType: "line",
            RoutedIntent: AgentIntent.DemandForecasting,
            MinSeries: 1,
            MinMarks: 2,
            RequiredEntities: ["Sierra Gold Tequila"],
            AxisUnit: "Depletion Volume",
            DataSource: ChartDataSource.HistoricalDemand),

        new(
            Prompt: "Show me a bar chart comparing depletion velocity for all spirits brands in the Northeast",
            ChartType: "bar",
            RoutedIntent: AgentIntent.DemandForecasting,
            MinSeries: 1,
            MinMarks: 3,
            RequiredEntities: ["Sierra Gold Tequila", "Ridgeline Bourbon", "Summit Vodka"],
            AxisUnit: "Avg Weekly Depletion Velocity",
            DataSource: ChartDataSource.HistoricalDemand),

        new(
            Prompt: "Create a pie chart showing market share breakdown for our grocery brands nationally",
            ChartType: "pie",
            RoutedIntent: AgentIntent.CompetitiveMarket,
            MinSeries: 1,
            MinMarks: 2,
            RequiredEntities: ["FreshMart", "Harvest Table"],
            AxisUnit: "Market Share %",
            DataSource: ChartDataSource.MarketShare,
            PercentAxis: true),

        new(
            Prompt: "Show a grouped bar chart comparing FreshMart and Harvest Table across all regions",
            ChartType: "groupedBar",
            RoutedIntent: AgentIntent.DemandForecasting,
            MinSeries: 2,
            MinMarks: 12,
            RequiredEntities: ["FreshMart", "Harvest Table"],
            AxisUnit: "Depletion Volume",
            DataSource: ChartDataSource.HistoricalDemand),

        new(
            Prompt: "Create a donut chart of Apex Grill variant mix in the Southwest",
            ChartType: "donut",
            RoutedIntent: AgentIntent.DemandForecasting,
            MinSeries: 1,
            MinMarks: 2,
            RequiredEntities: ["Apex Grill"],
            AxisUnit: "Variant Mix %",
            DataSource: ChartDataSource.VariantMix,
            PercentAxis: true),

        new(
            Prompt: "Show a horizontal bar chart ranking all brands by depletion growth rate",
            ChartType: "horizontalBar",
            RoutedIntent: AgentIntent.General,
            MinSeries: 1,
            MinMarks: 6,
            RequiredEntities: [],
            AxisUnit: "Depletion Growth Rate % (YoY)",
            DataSource: ChartDataSource.PortfolioDepletion,
            PercentAxis: true),

        new(
            Prompt: "Create a table showing depletion stats for all home improvement brands by region",
            ChartType: "table",
            RoutedIntent: AgentIntent.DemandForecasting,
            MinSeries: 1,
            MinMarks: 2,
            RequiredEntities: ["Pinnacle Hardware", "Summit Outdoor"],
            AxisUnit: "Depletion Stats",
            DataSource: ChartDataSource.DepletionStats),

        new(
            Prompt: "Show a gauge chart for Pinnacle Hardware inventory health in the Midwest",
            ChartType: "gauge",
            RoutedIntent: AgentIntent.SupplyShipments,
            MinSeries: 1,
            MinMarks: 1,
            RequiredEntities: ["Pinnacle Hardware"],
            AxisUnit: "Inventory Health % (0–100)",
            DataSource: ChartDataSource.InventoryLevels,
            PercentAxis: true),

        new(
            Prompt: "Compare Coastline Tacos vs Apex Grill depletions across all regions",
            ChartType: "groupedBar",
            RoutedIntent: AgentIntent.DemandForecasting,
            MinSeries: 2,
            MinMarks: 4,
            RequiredEntities: ["Coastline Tacos", "Apex Grill"],
            AxisUnit: "Depletion Volume",
            DataSource: ChartDataSource.HistoricalDemand),
    ];

    /// <summary>The verbatim curated chart-prompt texts, in manifest order.</summary>
    public static IReadOnlyList<string> Prompts => [.. Cases.Select(c => c.Prompt)];
}

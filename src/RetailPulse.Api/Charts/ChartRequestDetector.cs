using System.Text.RegularExpressions;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Charts;

/// <summary>
/// Result of inspecting a user message for an <b>explicit</b> visualization request.
/// </summary>
/// <param name="IsExplicitChartRequest">
/// True when the message unambiguously asks for a chart/graph/gauge/table visualization
/// (a chart-type word paired with a chart noun, a bare "gauge", or a visualization verb
/// paired with a chart noun).
/// </param>
/// <param name="ChartType">
/// The canonical <see cref="Contracts.ChartSpec.Type"/> the user asked for
/// (e.g. <c>bar</c>, <c>gauge</c>, <c>groupedBar</c>), or <c>null</c> when a chart was
/// requested without naming a specific type.
/// </param>
/// <param name="RoutedIntent">
/// The specialist <see cref="AgentIntent"/> best able to fetch the data AND call
/// <c>CreateChart</c> for this request. Never <see cref="AgentIntent.PortfolioHealth"/>:
/// an explicit chart request must reach a data+chart specialist, not the multi-agent
/// council (which produces prose/votes, no chart).
/// </param>
public readonly record struct ChartIntent(
    bool IsExplicitChartRequest,
    string? ChartType,
    string RoutedIntent);

/// <summary>
/// Deterministic, generic detector for explicit chart requests. Shared by the router
/// (to route a visualization request to a chart-capable specialist before broad keyword
/// or LLM classification can misroute it — e.g. sending "gauge chart for ... inventory
/// health" to the health council on the keyword <c>health</c>) and by the execution
/// pipeline (to enforce the chart-fulfillment invariant).
///
/// The mapping is intent-based and brand-agnostic: a chart-type word plus a domain/entity
/// cue selects the specialist whose tools can answer it, defaulting to the General agent
/// when the domain is unclear. It deliberately never maps to the portfolio-health council.
/// </summary>
public static partial class ChartRequestDetector
{
    // "bar chart", "gauge graph", "grouped bar chart", "line plot", ... → capture the type.
    [GeneratedRegex(
        @"\b(?<type>grouped\s*bar|stacked\s*bar|horizontal\s*bar|bar|line|pie|donut|doughnut|gauge|scatter|area|column|table)\s+(?:chart|graph|plot)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex TypedChartRegex();

    // A bare chart noun: "chart", "graph", "plot", "visualization", "visualisation".
    [GeneratedRegex(@"\b(?:chart|graph|plot|visuali[sz]ation)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChartNounRegex();

    // A visualization verb that, paired with a chart noun, signals an explicit request.
    [GeneratedRegex(
        @"\b(?:show|display|plot|graph|chart|draw|render|visuali[sz]e|create|generate|make|build|give\s+me)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex VizVerbRegex();

    // "gauge" is unambiguous in this domain — it only ever denotes a gauge chart.
    [GeneratedRegex(@"\bgauge\b", RegexOptions.IgnoreCase)]
    private static partial Regex GaugeRegex();

    // A standalone chart-type word (used when a chart noun/verb was found but no
    // "<type> chart" phrase, e.g. "show the depletion bars as a chart").
    [GeneratedRegex(
        @"\b(?<type>grouped\s*bar|stacked\s*bar|horizontal\s*bar|bar|line|pie|donut|doughnut|gauge|scatter|area|column|table)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex StandaloneTypeRegex();

    /// <summary>
    /// Domain/entity cue → specialist intent. Ordered by specificity so the most
    /// distinctive cue wins. Every mapped intent has a specialist that exposes
    /// <c>CreateChart</c>. <see cref="AgentIntent.PortfolioHealth"/> is intentionally
    /// absent — a chart request must never be routed to the council.
    /// </summary>
    private static readonly (string Intent, string[] Cues)[] _domainCues =
    [
        (AgentIntent.Planogram, ["planogram", "shelf", "facing", "sku placement", "aisle", "merchandis"]),
        (AgentIntent.MarginAnalysis, ["margin", "profit", "profitability", "cogs", "gross margin", "p&l", "pnl"]),
        (AgentIntent.PromotionTrade, ["promotion", "promo", "trade spend", "trade-spend", "deal effectiveness", "promotional"]),
        (AgentIntent.CompetitiveMarket, ["competitor", "competitive", "market share", "price war", "pricing pressure", "category share"]),
        (AgentIntent.SentimentField, ["sentiment", "field feedback", "field rep", "distributor feedback", "rep feedback", "satisfaction"]),
        (AgentIntent.StoreOps, ["store operations", "store performance", "foot traffic", "conversion rate", "underperforming store", "revenue vs target"]),
        // Supply/inventory: note "inventory health", "stock", "fulfillment", etc.
        (AgentIntent.SupplyShipments, ["inventory", "stock", "stockout", "stock out", "supply", "fulfillment", "fulfilment", "disruption", "warehouse", "on-hand", "on hand", "days of supply", "otif", "sell-in", "shipment", "pipeline health"]),
        // Demand/depletion/velocity.
        (AgentIntent.DemandForecasting, ["depletion", "deplete", "velocity", "demand", "forecast", "sell-through", "sell through", "sellthrough", "seasonal", "seasonality", "trend", "units sold"]),
        (AgentIntent.Scorecard, ["scorecard", "brand score", "performance ranking"]),
    ];

    /// <summary>
    /// Inspect <paramref name="message"/> for an explicit chart request and, when present,
    /// choose the specialist intent best able to fetch data and render the chart.
    /// </summary>
    public static ChartIntent Detect(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new ChartIntent(false, null, AgentIntent.General);
        }

        string? chartType = null;
        bool explicitRequest = false;

        Match typed = TypedChartRegex().Match(message);
        if (typed.Success)
        {
            explicitRequest = true;
            chartType = NormalizeType(typed.Groups["type"].Value);
        }
        else if (GaugeRegex().IsMatch(message))
        {
            // "gauge" alone unambiguously requests a gauge chart.
            explicitRequest = true;
            chartType = "gauge";
        }
        else if (ChartNounRegex().IsMatch(message) && VizVerbRegex().IsMatch(message))
        {
            explicitRequest = true;
            Match standalone = StandaloneTypeRegex().Match(message);
            chartType = standalone.Success ? NormalizeType(standalone.Groups["type"].Value) : null;
        }

        if (!explicitRequest)
        {
            return new ChartIntent(false, null, AgentIntent.General);
        }

        string routedIntent = ResolveDomainIntent(message);
        return new ChartIntent(true, chartType, routedIntent);
    }

    private static string ResolveDomainIntent(string message)
    {
        foreach ((string intent, string[] cues) in _domainCues)
        {
            foreach (string cue in cues)
            {
                if (message.Contains(cue, StringComparison.OrdinalIgnoreCase))
                {
                    return intent;
                }
            }
        }

        // Explicit chart but no clear domain — the General agent has broad tool access
        // (including CreateChart) and is the safe, chart-capable default.
        return AgentIntent.General;
    }

    /// <summary>
    /// Canonicalize a matched chart-type token to a <see cref="Contracts.ChartSpec.Type"/>
    /// value the renderer understands. Unknown-but-charty tokens fall back to <c>bar</c>.
    /// </summary>
    private static string NormalizeType(string raw)
    {
        string collapsed = WhitespaceRegex().Replace(raw.Trim().ToLowerInvariant(), " ");
        return collapsed switch
        {
            "grouped bar" => "groupedBar",
            "stacked bar" => "stackedBar",
            "horizontal bar" => "horizontalBar",
            "bar" or "column" => "bar",
            "line" or "area" => "line",
            "pie" => "pie",
            "donut" or "doughnut" => "donut",
            "gauge" => "gauge",
            "table" => "table",
            _ => "bar",
        };
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

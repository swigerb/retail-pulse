using System.ComponentModel;
using ModelContextProtocol.Server;
using RetailPulse.McpServer.Data;

namespace RetailPulse.McpServer.Tools;

[McpServerToolType]
public static class DemandTools
{
    [McpServerTool(Name = "GetHistoricalDemand")]
    [Description("Get historical demand/depletion data aggregated by week. Returns volume and units over time, filterable by brand, region, channel, and number of months. Use this for trend analysis, baseline comparisons, and demand pattern identification.")]
    public static object GetHistoricalDemand(
        RetailPulseDb data,
        [Description("Brand name to filter (e.g. 'Sierra Gold Tequila', 'FreshMart'). Omit for all brands.")] string? brand = null,
        [Description("Region to filter (e.g. 'Northeast', 'West Coast'). Omit for all regions.")] string? region = null,
        [Description("Channel to filter ('On-Premise', 'Off-Premise', 'E-Commerce'). Omit for all channels.")] string? channel = null,
        [Description("Number of months of history to return (1-24). Default: 12")] int months = 12) => data.GetHistoricalDemand(brand, region, channel, months);

    [McpServerTool(Name = "GenerateForecast")]
    [Description("Generate a demand forecast for a brand using trailing average + seasonal multipliers + trend analysis. Returns daily predicted volume with ±15% confidence bounds and explanation of which seasonal factors were applied.")]
    public static object GenerateForecast(
        RetailPulseDb data,
        [Description("Brand name (required, e.g. 'Ridgeline Bourbon', 'Apex Grill')")] string brand,
        [Description("Region to forecast (e.g. 'Southwest'). Omit to forecast across all regions.")] string? region = null,
        [Description("Number of days to forecast (7-365). Default: 90")] int days = 90)
    {
        return string.IsNullOrWhiteSpace(brand)
            ? (new { error = "Parameter 'brand' is required." })
            : data.GenerateForecast(brand, region, days);
    }

    [McpServerTool(Name = "GetSeasonalityFactors")]
    [Description("Get seasonal demand multipliers by month for product categories. Shows which months see boosted or reduced demand and why (holidays, back-to-school, summer, etc.). Useful for understanding demand patterns and planning.")]
    public static object GetSeasonalityFactors(
        RetailPulseDb data,
        [Description("Product category to filter (e.g. 'Spirits', 'Grocery', 'Home Improvement'). Omit for all categories.")] string? category = null) => data.GetSeasonalityFactors(category);

    [McpServerTool(Name = "IdentifyDemandRisks")]
    [Description("Analyze recent demand data for anomalies and risks. Detects sudden drops (>20%), unusual spikes, and trend reversals over the last 90 days. Returns risks ranked by severity (high/medium/low) with affected periods.")]
    public static object IdentifyDemandRisks(
        RetailPulseDb data,
        [Description("Brand name to analyze (e.g. 'Summit Vodka'). Omit for all brands.")] string? brand = null,
        [Description("Region to analyze (e.g. 'Midwest'). Omit for all regions.")] string? region = null) => data.IdentifyDemandRisks(brand, region);
}

using System.ComponentModel;
using ModelContextProtocol.Server;
using RetailPulse.McpServer.Data;

namespace RetailPulse.McpServer.Tools;

[McpServerToolType]
public static class CompetitiveTools
{
    [McpServerTool(Name = "GetCompetitorPricing")]
    [Description("Get current and historical competitor pricing data. Returns competitor prices, price changes, and identifies aggressive price drops (>10%). Filterable by brand, category, region, and specific competitors.")]
    public static object GetCompetitorPricing(
        RetailPulseDb data,
        [Description("Brand name to filter (e.g. 'Sierra Gold Tequila'). Omit for all brands.")] string? brand = null,
        [Description("Category to filter (e.g. 'Spirits', 'Grocery'). Omit for all categories.")] string? category = null,
        [Description("Region to filter (e.g. 'Northeast'). Omit for all regions.")] string? region = null,
        [Description("Comma-separated competitor names (e.g. 'Jack Daniel\\'s,Patrón'). Omit for all.")] string? competitors = null) => data.GetCompetitorPricing(brand, category, region, competitors);

    [McpServerTool(Name = "GetMarketShare")]
    [Description("Get market share trends over time. Returns quarterly share data with period-over-period changes. Identifies significant share losses (>2 points). Filterable by brand, category, region, and time period. Pass region='National' for a nationwide rollup (the unweighted mean of each brand's regional shares); pass a specific region for that region's rows.")]
    public static object GetMarketShare(
        RetailPulseDb data,
        [Description("Brand name to filter. Omit for all brands.")] string? brand = null,
        [Description("Category to filter. Omit for all categories.")] string? category = null,
        [Description("Region to filter, or 'National' for a nationwide rollup. Omit for all regions.")] string? region = null,
        [Description("Period to filter (e.g. '2026-Q1', '2025-Q4'). Omit for all periods.")] string? period = null) => data.GetMarketShare(brand, category, region, period);

    [McpServerTool(Name = "DetectThreats")]
    [Description("Identify competitive threats including price drops >10%, market share losses >2 points, and high-impact competitor activities. Returns threats ranked by severity with defensive recommendations (MATCH, DIFFERENTIATE, PREEMPT, IGNORE).")]
    public static object DetectThreats(
        RetailPulseDb data,
        [Description("Brand name to scope threats. Omit for all brands.")] string? brand = null,
        [Description("Category to scope threats. Omit for all categories.")] string? category = null,
        [Description("Region to scope threats. Omit for all regions.")] string? region = null) => data.DetectCompetitiveThreats(brand, category, region);

    [McpServerTool(Name = "GetCompetitiveLandscape")]
    [Description("Get a full competitive overview for a category and region. Returns market share positions for all players, recent competitive activities, and pricing moves. Use to understand the overall competitive environment.")]
    public static object GetCompetitiveLandscape(
        RetailPulseDb data,
        [Description("Category (required, e.g. 'Spirits', 'Grocery', 'Furniture')")] string category,
        [Description("Region (required, e.g. 'Northeast', 'West Coast')")] string region)
    {
        if (string.IsNullOrWhiteSpace(category))
            return new { error = "Parameter 'category' is required." };
        return string.IsNullOrWhiteSpace(region)
            ? (new { error = "Parameter 'region' is required." })
            : data.GetCompetitiveLandscape(category, region);
    }
}

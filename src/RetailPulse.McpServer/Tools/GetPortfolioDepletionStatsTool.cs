using System.ComponentModel;
using ModelContextProtocol.Server;
using RetailPulse.McpServer.Data;

namespace RetailPulse.McpServer.Tools;

[McpServerToolType]
public static class GetPortfolioDepletionStatsTool
{
    [McpServerTool(Name = "GetPortfolioDepletionStats")]
    [Description("Get depletion statistics for MULTIPLE brands in a single call. Use this for portfolio-wide comparisons, category rollups, and cross-brand analysis instead of calling GetDepletionStats per brand. Supports optional category filter (e.g. 'Home Improvement') to scope to one tenant category, an optional explicit brand list to compare a small set (e.g. 'Foundry Home,Urban Living'), and 'AllRegions' region to return per-region rows for every region in one call ('All' is treated as the 'National' single aggregate — use 'AllRegions' when you want per-region fan-out).")]
    public static object GetPortfolioDepletionStats(
        RetailPulseDb data,
        [Description("Region axis control: a specific region name ('Northeast','Southeast','Midwest','Southwest','West Coast','Pacific Northwest') returns one row per brand for that region; 'National' returns the single national aggregate per brand; 'AllRegions' (or 'ByRegion') fans out — one row per brand PER tenant region — for a 'by region' or 'across all regions' table in one call. NOTE: 'All' is aliased to 'National' (single aggregate), NOT to fan-out; use 'AllRegions' for per-region breakdowns.")] string region,
        [Description("Period (e.g. 'YTD', 'Q1', 'Q2')")] string period = "YTD",
        [Description("Optional category filter (e.g. 'Home Improvement', 'Spirits', 'Furniture'). When set, only brands in this tenant category are returned. Omit for all categories.")] string? category = null,
        [Description("Optional comma-separated brand list to restrict the answer to a specific comparison set (e.g. 'Foundry Home,Urban Living'). Omit to return every brand allowed by the category filter.")] string? brands = null)
    {
        if (string.IsNullOrWhiteSpace(region))
            return new { error = "Parameter 'region' is required." };
        if (string.IsNullOrWhiteSpace(period))
            period = "YTD";

        return data.GetPortfolioDepletionStats(region, period, category, brands);
    }
}

using System.ComponentModel;
using ModelContextProtocol.Server;
using RetailPulse.McpServer.Data;

namespace RetailPulse.McpServer.Tools;

[McpServerToolType]
public static class MarginTools
{
    [McpServerTool(Name = "GetMarginByBrand")]
    [Description("Get P&L breakdown by brand including revenue, COGS, marketing, distribution, and margin percentages. Filter by period (e.g. '2026-Q1').")]
    public static object GetMarginByBrand(
        RetailPulseDb data,
        [Description("Brand name (required, e.g. 'Sierra Gold Tequila')")] string brand,
        [Description("Period filter (e.g. '2026-Q1'). Omit for all periods.")] string? period = null)
    {
        if (string.IsNullOrWhiteSpace(brand)) return new { error = "Parameter 'brand' is required." };
        return data.GetMarginByBrand(brand, period);
    }

    [McpServerTool(Name = "GetMarginDrivers")]
    [Description("Identify what's driving margin up or down for a brand. Returns cost categories, amounts, impact percentages, and trends.")]
    public static object GetMarginDrivers(
        RetailPulseDb data,
        [Description("Brand name (required)")] string brand)
    {
        if (string.IsNullOrWhiteSpace(brand)) return new { error = "Parameter 'brand' is required." };
        return data.GetMarginDrivers(brand);
    }

    [McpServerTool(Name = "GetMarginTrend")]
    [Description("Get margin trajectory over time for a brand. Shows gross and net margin trends across quarters.")]
    public static object GetMarginTrend(
        RetailPulseDb data,
        [Description("Brand name (required)")] string brand,
        [Description("Number of quarters to show (default 4)")] int quarters = 4)
    {
        if (string.IsNullOrWhiteSpace(brand)) return new { error = "Parameter 'brand' is required." };
        return data.GetMarginTrend(brand, quarters);
    }

    [McpServerTool(Name = "DetectMarginRisks")]
    [Description("Identify margin-destructive patterns: cost escalation, margin compression, negative net margins. Returns ranked risks with recommendations.")]
    public static object DetectMarginRisks(
        RetailPulseDb data,
        [Description("Brand name to scope. Omit for all brands.")] string? brand = null)
    {
        return data.DetectMarginRisks(brand);
    }
}

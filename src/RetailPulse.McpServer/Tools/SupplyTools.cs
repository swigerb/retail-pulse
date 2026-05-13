using System.ComponentModel;
using ModelContextProtocol.Server;
using RetailPulse.McpServer.Data;

namespace RetailPulse.McpServer.Tools;

[McpServerToolType]
public static class SupplyTools
{
    [McpServerTool(Name = "GetInventoryLevels")]
    [Description("Get current inventory levels by brand, region, and category. Returns SKU-level stock, safety stock, days of supply, and status (healthy/low/critical/out_of_stock). Use for inventory health checks and stockout risk identification.")]
    public static object GetInventoryLevels(
        RetailPulseDb data,
        [Description("Brand name to filter (e.g. 'Sierra Gold Tequila', 'FreshMart'). Omit for all brands.")] string? brand = null,
        [Description("Region to filter (e.g. 'Northeast', 'West Coast'). Omit for all regions.")] string? region = null,
        [Description("Category to filter (e.g. 'Spirits', 'Grocery'). Omit for all categories.")] string? category = null,
        [Description("Status filter: 'healthy', 'low', 'critical', 'out_of_stock'. Omit for all.")] string? status = null)
    {
        return data.GetInventoryLevels(brand, region, category, status);
    }

    [McpServerTool(Name = "GetSupplyDisruptions")]
    [Description("Get active supply chain disruptions. Returns disruption type (logistics/supplier/weather/demand_surge), severity, impacted SKUs, and estimated resolution. Use for risk assessment and supply chain visibility.")]
    public static object GetSupplyDisruptions(
        RetailPulseDb data,
        [Description("Brand name to filter. Omit for all brands.")] string? brand = null,
        [Description("Region to filter. Omit for all regions.")] string? region = null,
        [Description("Severity filter: 'high', 'medium', 'low'. Omit for all.")] string? severity = null,
        [Description("Show only active disruptions (default: true).")] bool activeOnly = true)
    {
        return data.GetSupplyDisruptions(brand, region, severity, activeOnly);
    }

    [McpServerTool(Name = "GetFulfillmentRate")]
    [Description("Get order fulfillment rate trends over time. Returns fill rate %, on-time delivery %, and backorder counts by period. Use for service level assessment and trend detection.")]
    public static object GetFulfillmentRate(
        RetailPulseDb data,
        [Description("Brand name to filter. Omit for all brands.")] string? brand = null,
        [Description("Region to filter. Omit for all regions.")] string? region = null,
        [Description("Specific period to filter (e.g. '2026-04'). Omit for all periods.")] string? period = null,
        [Description("Minimum number of periods to return (1-12). Default: 6")] int minPeriods = 6)
    {
        return data.GetFulfillmentRates(brand, region, period, minPeriods);
    }

    [McpServerTool(Name = "GetSupplyHealthSummary")]
    [Description("Get an aggregate supply chain health summary combining inventory status, active disruptions, and fulfillment rates into an overall assessment (Green/Yellow/Red). Use for executive-level health overviews.")]
    public static object GetSupplyHealthSummary(
        RetailPulseDb data,
        [Description("Brand name (required)")] string brand,
        [Description("Region to scope (e.g. 'Northeast'). Omit for all regions.")] string? region = null)
    {
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required." };

        return data.GetSupplyHealthSummary(brand, region);
    }
}

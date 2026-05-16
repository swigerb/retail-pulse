using System.ComponentModel;
using ModelContextProtocol.Server;
using RetailPulse.McpServer.Data;

namespace RetailPulse.McpServer.Tools;

[McpServerToolType]
public static class StoreOpsTools
{
    [McpServerTool(Name = "GetStorePerformance")]
    [Description("Get store performance metrics including revenue vs target, foot traffic, and conversion rates. Identifies underperforming stores. Filter by region or specific store ID.")]
    public static object GetStorePerformance(
        RetailPulseDb data,
        [Description("Region to filter (e.g. 'Northeast'). Omit for all regions.")] string? region = null,
        [Description("Specific store ID (e.g. 'STR-0001'). Omit for all stores.")] string? storeId = null) => data.GetStorePerformance(region, storeId);

    [McpServerTool(Name = "GetShelfLayout")]
    [Description("Get the current planogram/shelf layout for a specific aisle in a store. Returns SKU positions, shelf levels, and facing widths.")]
    public static object GetShelfLayout(
        RetailPulseDb data,
        [Description("Store ID (required, e.g. 'STR-0001')")] string storeId,
        [Description("Aisle ID (required, e.g. 'AISLE-STR-0001-01')")] string aisleId)
    {
        if (string.IsNullOrWhiteSpace(storeId)) return new { error = "Parameter 'storeId' is required." };
        return string.IsNullOrWhiteSpace(aisleId)
            ? (new { error = "Parameter 'aisleId' is required." })
            : data.GetShelfLayout(storeId, aisleId);
    }

    [McpServerTool(Name = "OptimizePlanogram")]
    [Description("Generate an optimized planogram layout for an aisle. Returns predicted revenue uplift percentage and optimization recommendations.")]
    public static object OptimizePlanogram(
        RetailPulseDb data,
        [Description("Store ID (required)")] string storeId,
        [Description("Aisle ID (required)")] string aisleId)
    {
        if (string.IsNullOrWhiteSpace(storeId)) return new { error = "Parameter 'storeId' is required." };
        return string.IsNullOrWhiteSpace(aisleId)
            ? (new { error = "Parameter 'aisleId' is required." })
            : data.OptimizePlanogram(storeId, aisleId);
    }

    [McpServerTool(Name = "PredictStockout")]
    [Description("Predict days until stockout for SKUs at a store. Returns risk levels (critical/high/medium/low) and current velocity data.")]
    public static object PredictStockout(
        RetailPulseDb data,
        [Description("Store ID (required)")] string storeId,
        [Description("Specific SKU ID to check. Omit for all SKUs.")] string? skuId = null) => string.IsNullOrWhiteSpace(storeId) ? (new { error = "Parameter 'storeId' is required." }) : data.PredictStockout(storeId, skuId);
}

using System.ComponentModel;
using ModelContextProtocol.Server;
using RetailPulse.McpServer.Data;

namespace RetailPulse.McpServer.Tools;

[McpServerToolType]
public static class GetShipmentStatsTool
{
    [McpServerTool(Name = "GetShipmentStats")]
    [Description("Get shipment statistics (sell-in from supplier to distributors) for a brand in a specific region. Includes anomaly detection for pipeline clogs where shipments outpace sell-through.")]
    public static object GetShipmentStats(
        SimulatedMetricsData data,
        [Description("Brand name (e.g. 'brand name')")] string brand,
        [Description("Region (e.g. 'Northeast', 'West Coast', 'National')")] string region,
        [Description("Period (e.g. 'YTD', 'Q1', 'Q2')")] string period = "YTD")
    {
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required." };
        if (string.IsNullOrWhiteSpace(region))
            return new { error = "Parameter 'region' is required." };
        if (string.IsNullOrWhiteSpace(period))
            period = "YTD";

        return data.GetShipmentStats(brand, region, period);
    }
}

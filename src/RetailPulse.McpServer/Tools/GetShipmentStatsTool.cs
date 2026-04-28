using System.ComponentModel;
using ModelContextProtocol.Server;
using RetailPulse.McpServer.Data;

namespace RetailPulse.McpServer.Tools;

[McpServerToolType]
public static class GetShipmentStatsTool
{
    [McpServerTool(Name = "GetShipmentStats")]
    [Description("Get shipment statistics (sell-in from Bacardi to distributors) for a brand in a specific region. Includes anomaly detection for pipeline clogs where shipments outpace sell-through.")]
    public static object GetShipmentStats(
        [Description("Brand name (e.g. 'Patron Silver', 'Grey Goose')")] string brand,
        [Description("Region (e.g. 'Florida', 'Texas', 'National')")] string region,
        [Description("Period (e.g. 'YTD', 'Q1', 'Q2')")] string period = "YTD")
    {
        return BacardiSimulatedData.GetShipmentStats(brand, region, period);
    }
}

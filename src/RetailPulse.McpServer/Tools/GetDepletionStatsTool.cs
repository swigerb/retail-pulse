using System.ComponentModel;
using ModelContextProtocol.Server;
using RetailPulse.McpServer.Data;

namespace RetailPulse.McpServer.Tools;

[McpServerToolType]
public static class GetDepletionStatsTool
{
    [McpServerTool(Name = "GetDepletionStats")]
    [Description("Get depletion statistics for a company brand in a specific region and time period")]
    public static object GetDepletionStats(
        SimulatedMetricsData data,
        [Description("Brand name (e.g. 'brand name')")] string brand,
        [Description("Region (e.g. 'Northeast', 'West Coast', 'National')")] string region,
        [Description("Period (e.g. 'YTD', 'Q1', 'Q2')")] string period = "YTD")
    {
        return data.GetDepletionStats(brand, region, period);
    }
}

using System.ComponentModel;
using ModelContextProtocol.Server;
using RetailPulse.McpServer.Data;

namespace RetailPulse.McpServer.Tools;

[McpServerToolType]
public static class GetDepletionStatsTool
{
    [McpServerTool(Name = "GetDepletionStats")]
    [Description("Get depletion statistics for a Bacardi brand in a specific region and time period")]
    public static object GetDepletionStats(
        [Description("Brand name (e.g. 'Patron Silver', 'Bacardi Superior')")] string brand,
        [Description("Region (e.g. 'Florida', 'Texas', 'National')")] string region,
        [Description("Period (e.g. 'YTD', 'Q1', 'Q2')")] string period = "YTD")
    {
        return BacardiSimulatedData.GetDepletionStats(brand, region, period);
    }
}

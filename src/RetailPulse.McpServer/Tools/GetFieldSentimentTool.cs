using System.ComponentModel;
using ModelContextProtocol.Server;
using RetailPulse.McpServer.Data;

namespace RetailPulse.McpServer.Tools;

[McpServerToolType]
public static class GetFieldSentimentTool
{
    [McpServerTool(Name = "GetFieldSentiment")]
    [Description("Get field sentiment and distributor feedback for a company brand in a region")]
    public static object GetFieldSentiment(
        RetailPulseDb data,
        [Description("Brand name")] string brand,
        [Description("Region")] string region)
    {
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required." };
        return string.IsNullOrWhiteSpace(region) ? (new { error = "Parameter 'region' is required." }) : data.GetFieldSentiment(brand, region);
    }
}

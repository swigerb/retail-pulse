using System.ComponentModel;
using ModelContextProtocol.Server;
using RetailPulse.McpServer.Data;

namespace RetailPulse.McpServer.Tools;

[McpServerToolType]
public static class GetPortfolioDepletionStatsTool
{
    [McpServerTool(Name = "GetPortfolioDepletionStats")]
    [Description("Get depletion statistics for ALL brands in the portfolio in a single call. Use this for portfolio-wide comparisons, rankings, and cross-brand analysis instead of calling GetDepletionStats per brand.")]
    public static object GetPortfolioDepletionStats(
        SimulatedMetricsData data,
        [Description("Region (e.g. 'Northeast', 'West Coast', 'National')")] string region,
        [Description("Period (e.g. 'YTD', 'Q1', 'Q2')")] string period = "YTD")
    {
        if (string.IsNullOrWhiteSpace(region))
            return new { error = "Parameter 'region' is required." };
        if (string.IsNullOrWhiteSpace(period))
            period = "YTD";

        return data.GetPortfolioDepletionStats(region, period);
    }
}

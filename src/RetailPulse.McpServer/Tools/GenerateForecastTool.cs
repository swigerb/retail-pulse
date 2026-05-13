using System.ComponentModel;
using ModelContextProtocol.Server;
using RetailPulse.McpServer.Data;

namespace RetailPulse.McpServer.Tools;

[McpServerToolType]
public static class GenerateForecastTool
{
    [McpServerTool(Name = "GenerateForecast")]
    [Description("Generate a 90-day demand forecast with confidence intervals for a brand. Uses historical trends, seasonality, and category patterns to predict future volume.")]
    public static object GenerateForecast(
        RetailPulseDb data,
        [Description("Brand name (e.g. 'Sierra Gold Tequila')")] string brand,
        [Description("Region (e.g. 'Northeast', 'National'). Defaults to 'National'.")] string region = "National",
        [Description("Channel (e.g. 'On-Premise', 'Off-Premise', 'E-Commerce', 'All'). Defaults to 'All'.")] string channel = "All")
    {
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required." };

        return data.GenerateForecast(brand, region);
    }
}

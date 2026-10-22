using System.ComponentModel;
using ModelContextProtocol.Server;
using RetailPulse.McpServer.Data;

namespace RetailPulse.McpServer.Tools;

[McpServerToolType]
public static class GetHistoricalDemandTool
{
    [McpServerTool(Name = "GetHistoricalDemand")]
    [Description("Get historical depletion/demand data for a brand over an explicit dataset-relative period.")]
    public static object GetHistoricalDemand(
        RetailPulseDb data,
        [Description("Brand name (e.g. 'Sierra Gold Tequila')")] string brand,
        [Description("Region (e.g. 'Northeast', 'National'). Defaults to 'National'.")] string region = "National",
        [Description("Channel (e.g. 'On-Premise', 'Off-Premise', 'E-Commerce', 'All'). Defaults to 'All'.")] string channel = "All",
        [Description("Supported period: current-month, current-quarter, current-year, or last-N-months where N is 1..24.")] string period = "last-12-months")
    {
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required." };

        string? effectiveRegion = string.Equals(region, "National", StringComparison.OrdinalIgnoreCase) ? null : region;
        string? effectiveChannel = string.Equals(channel, "All", StringComparison.OrdinalIgnoreCase) ? null : channel;

        return data.GetHistoricalDemand(brand, effectiveRegion, effectiveChannel, period: period);
    }
}

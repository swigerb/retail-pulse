using System.ComponentModel;
using ModelContextProtocol.Server;
using RetailPulse.McpServer.Data;

namespace RetailPulse.McpServer.Tools;

[McpServerToolType]
public static class GetHistoricalDemandTool
{
    [McpServerTool(Name = "GetHistoricalDemand")]
    [Description("Get 12 months of historical depletion/demand data for a brand, optionally filtered by region and channel. Returns monthly volume and units for trend analysis.")]
    public static object GetHistoricalDemand(
        RetailPulseDb data,
        [Description("Brand name (e.g. 'Sierra Gold Tequila')")] string brand,
        [Description("Region (e.g. 'Northeast', 'National'). Defaults to 'National'.")] string region = "National",
        [Description("Channel (e.g. 'On-Premise', 'Off-Premise', 'E-Commerce', 'All'). Defaults to 'All'.")] string channel = "All")
    {
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required." };

        string? effectiveRegion = string.Equals(region, "National", StringComparison.OrdinalIgnoreCase) ? null : region;
        string? effectiveChannel = string.Equals(channel, "All", StringComparison.OrdinalIgnoreCase) ? null : channel;

        return data.GetHistoricalDemand(brand, effectiveRegion, effectiveChannel);
    }
}

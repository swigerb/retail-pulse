using System.ComponentModel;
using ModelContextProtocol.Server;
using RetailPulse.McpServer.Data;

namespace RetailPulse.McpServer.Tools;

[McpServerToolType]
public static class GetVariantMixTool
{
    [McpServerTool(Name = "GetVariantMix")]
    [Description("Get variant mix percentages and YoY depletion performance for each variant of a brand in a specific region")]
    public static object GetVariantMix(
        RetailPulseDb data,
        [Description("Brand name (e.g. 'Apex Grill')")] string brand,
        [Description("Region (e.g. 'Northeast', 'Southwest', 'National'). Defaults to 'National'.")] string region = "National")
    {
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required." };

        if (string.IsNullOrWhiteSpace(region))
            region = "National";

        return data.GetVariantMix(brand, region);
    }
}

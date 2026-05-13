using System.ComponentModel;
using ModelContextProtocol.Server;
using RetailPulse.McpServer.Data;

namespace RetailPulse.McpServer.Tools;

[McpServerToolType]
public static class IdentifyDemandRisksTool
{
    [McpServerTool(Name = "IdentifyDemandRisks")]
    [Description("Identify demand risks including trend breaks, anomalies, and supply/demand mismatches for a brand. Analyzes historical patterns to flag potential issues.")]
    public static object IdentifyDemandRisks(
        RetailPulseDb data,
        [Description("Brand name (e.g. 'Sierra Gold Tequila')")] string brand,
        [Description("Region (e.g. 'Northeast', 'National'). Defaults to 'National'.")] string region = "National")
    {
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required." };

        return data.IdentifyDemandRisks(brand, region);
    }
}

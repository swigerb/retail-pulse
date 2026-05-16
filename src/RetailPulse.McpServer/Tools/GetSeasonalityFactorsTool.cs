using System.ComponentModel;
using ModelContextProtocol.Server;
using RetailPulse.McpServer.Data;

namespace RetailPulse.McpServer.Tools;

[McpServerToolType]
public static class GetSeasonalityFactorsTool
{
    [McpServerTool(Name = "GetSeasonalityFactors")]
    [Description("Get seasonal multipliers by category and month. Shows how holidays, summer, back-to-school, and other events affect demand for each product category.")]
    public static object GetSeasonalityFactors(
        RetailPulseDb data,
        [Description("Product category (e.g. 'Spirits', 'Grocery', 'Quick-Serve Restaurant', 'All'). Defaults to 'All'.")] string category = "All")
    {
        string? effectiveCategory = string.Equals(category, "All", StringComparison.OrdinalIgnoreCase) ? null : category;
        return data.GetSeasonalityFactors(effectiveCategory);
    }
}

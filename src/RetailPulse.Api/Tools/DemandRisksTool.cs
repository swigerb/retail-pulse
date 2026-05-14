using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

[Obsolete("Use MCP demand tool instead. Will be removed in v2.")]
public class DemandRisksTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DemandRisksTool>? _logger;

    public DemandRisksTool(HttpClient httpClient, ILogger<DemandRisksTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Identify demand risks including trend breaks, anomalies, and supply/demand mismatches for a brand. Flags potential issues based on historical pattern analysis.")]
    public async Task<string> IdentifyDemandRisks(
        [Description("The brand name, e.g. 'Sierra Gold Tequila'")] string brand,
        [Description("The region, e.g. 'Northeast', 'National'. Defaults to 'National'.")] string region = "National",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/demand-risks?brand={Uri.EscapeDataString(brand)}&region={Uri.EscapeDataString(region)}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "DemandRisksTool failed for {Brand}/{Region} — returning fallback", brand, region);
            return JsonSerializer.Serialize(new
            {
                brand,
                region,
                error = "Demand risk analysis unavailable — MCP server not reachable.",
                risks = Array.Empty<object>()
            });
        }
    }
}

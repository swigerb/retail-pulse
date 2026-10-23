using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

public class DepletionStatsTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DepletionStatsTool>? _logger;

    public DepletionStatsTool(HttpClient httpClient, ILogger<DepletionStatsTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Get depletion statistics for a company brand in a specific region and time period. Returns sales velocity, year-over-year trends, inventory levels, and stock status.")]
    public async Task<string> GetDepletionStats(
        [Description("The brand name, e.g. 'Apex Grill', 'FreshMart', 'Sierra Gold Tequila'")] string brand,
        [Description("The region, e.g. 'Southwest', 'West Coast', 'National'")] string region,
        [Description("The time period, e.g. 'YTD', 'Q1', 'Q2', 'Last12Months'. Defaults to 'YTD' when omitted.")] string period = "YTD",
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpResponseMessage response = await _httpClient.GetAsync(
                $"/api/depletion-stats?brand={Uri.EscapeDataString(brand)}&region={Uri.EscapeDataString(region)}&period={Uri.EscapeDataString(period)}",
                cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Keep transport failures explicit so callers cannot mistake them for metrics.
            _logger?.LogWarning(ex, "DepletionStatsTool failed for brand {Brand}/{Region}/{Period} — returning explicit error", brand, region, period);
            return JsonSerializer.Serialize(new
            {
                brand,
                region,
                period,
                error = "Depletion statistics unavailable — MCP server not reachable."
            });
        }
    }
}

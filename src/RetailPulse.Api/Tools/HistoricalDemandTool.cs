using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

public class HistoricalDemandTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HistoricalDemandTool>? _logger;

    public HistoricalDemandTool(HttpClient httpClient, ILogger<HistoricalDemandTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Get 12 months of historical depletion/demand data for a brand. Returns monthly volume and units for trend analysis, seasonality detection, and forecasting.")]
    public async Task<string> GetHistoricalDemand(
        [Description("The brand name, e.g. 'Sierra Gold Tequila'")] string brand,
        [Description("The region, e.g. 'Northeast', 'National'. Defaults to 'National'.")] string region = "National",
        [Description("The channel, e.g. 'On-Premise', 'Off-Premise', 'E-Commerce', 'All'. Defaults to 'All'.")] string channel = "All",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/historical-demand?brand={Uri.EscapeDataString(brand)}&region={Uri.EscapeDataString(region)}&channel={Uri.EscapeDataString(channel)}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "HistoricalDemandTool failed for {Brand}/{Region}/{Channel} — returning fallback", brand, region, channel);
            return JsonSerializer.Serialize(new
            {
                brand,
                region,
                channel,
                error = "Historical demand data unavailable — MCP server not reachable.",
                monthly_data = Array.Empty<object>()
            });
        }
    }
}

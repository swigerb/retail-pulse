using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

[Obsolete("Use MCP demand tool instead. Will be removed in v2.")]
public class ForecastTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ForecastTool>? _logger;

    public ForecastTool(HttpClient httpClient, ILogger<ForecastTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Generate a 90-day demand forecast with confidence intervals for a brand. Uses historical trends, seasonality, and category patterns to predict future volume.")]
    public async Task<string> GenerateForecast(
        [Description("The brand name, e.g. 'Sierra Gold Tequila'")] string brand,
        [Description("The region, e.g. 'Northeast', 'National'. Defaults to 'National'.")] string region = "National",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/forecast?brand={Uri.EscapeDataString(brand)}&region={Uri.EscapeDataString(region)}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ForecastTool failed for {Brand}/{Region} — returning fallback", brand, region);
            return JsonSerializer.Serialize(new
            {
                brand,
                region,
                error = "Forecast data unavailable — MCP server not reachable.",
                predicted = Array.Empty<object>()
            });
        }
    }
}

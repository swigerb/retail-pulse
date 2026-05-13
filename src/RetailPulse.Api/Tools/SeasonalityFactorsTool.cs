using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

public class SeasonalityFactorsTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SeasonalityFactorsTool>? _logger;

    public SeasonalityFactorsTool(HttpClient httpClient, ILogger<SeasonalityFactorsTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Get seasonal multipliers by category and month. Shows how holidays, summer, back-to-school, and other events affect demand for each product category.")]
    public async Task<string> GetSeasonalityFactors(
        [Description("Product category, e.g. 'Spirits', 'Grocery', 'Quick-Serve Restaurant', 'All'. Defaults to 'All'.")] string category = "All",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/seasonality-factors?category={Uri.EscapeDataString(category)}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SeasonalityFactorsTool failed for {Category} — returning fallback", category);
            return JsonSerializer.Serialize(new
            {
                category,
                error = "Seasonality data unavailable — MCP server not reachable.",
                factors = Array.Empty<object>()
            });
        }
    }
}

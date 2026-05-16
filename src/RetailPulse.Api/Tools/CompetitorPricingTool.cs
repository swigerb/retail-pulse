using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

public class CompetitorPricingTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CompetitorPricingTool>? _logger;

    public CompetitorPricingTool(HttpClient httpClient, ILogger<CompetitorPricingTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Get current and historical competitor pricing data. Returns competitor prices, price changes, and identifies aggressive price drops (>10%).")]
    public async Task<string> GetCompetitorPricing(
        [Description("Brand name to filter. Omit for all brands.")] string? brand = null,
        [Description("Category to filter (e.g. 'Spirits', 'Grocery'). Omit for all.")] string? category = null,
        [Description("Region to filter. Omit for all regions.")] string? region = null,
        [Description("Comma-separated competitor names. Omit for all.")] string? competitors = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string url = "/api/competitive/pricing?";
            if (!string.IsNullOrWhiteSpace(brand)) url += $"&brand={Uri.EscapeDataString(brand)}";
            if (!string.IsNullOrWhiteSpace(category)) url += $"&category={Uri.EscapeDataString(category)}";
            if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";
            if (!string.IsNullOrWhiteSpace(competitors)) url += $"&competitors={Uri.EscapeDataString(competitors)}";

            HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "CompetitorPricingTool failed — returning fallback");
            return JsonSerializer.Serialize(new { error = "Competitor pricing unavailable — MCP server not reachable.", pricing = Array.Empty<object>() });
        }
    }
}

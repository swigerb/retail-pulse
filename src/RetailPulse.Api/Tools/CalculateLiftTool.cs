using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

public class CalculateLiftTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CalculateLiftTool>? _logger;

    public CalculateLiftTool(HttpClient httpClient, ILogger<CalculateLiftTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Calculate expected volume lift for a proposed promotion. Accounts for diminishing returns when spend exceeds optimal levels.")]
    public async Task<string> CalculateLift(
        [Description("Brand name (required)")] string brand,
        [Description("Region (required)")] string region,
        [Description("Promo type: 'discount', 'bogo', 'display', 'digital', 'bundle'")] string promoType,
        [Description("Planned spend in dollars")] double spend,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string url = $"/api/promo/calculate-lift?brand={Uri.EscapeDataString(brand)}&region={Uri.EscapeDataString(region)}&promoType={Uri.EscapeDataString(promoType)}&spend={spend}";
            HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "CalculateLiftTool failed for {Brand}/{Region}/{PromoType} — returning fallback", brand, region, promoType);
            return JsonSerializer.Serialize(new { brand, region, promo_type = promoType, spend, error = "Lift calculation unavailable — MCP server not reachable." });
        }
    }
}

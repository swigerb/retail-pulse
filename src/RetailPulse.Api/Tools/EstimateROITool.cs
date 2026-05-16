using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

public class EstimateROITool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<EstimateROITool>? _logger;

    public EstimateROITool(HttpClient httpClient, ILogger<EstimateROITool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Estimate full ROI for a proposed promotion combining lift, timing, and spend effectiveness. Returns expected ROI with confidence bounds and breakeven analysis.")]
    public async Task<string> EstimateROI(
        [Description("Brand name (required)")] string brand,
        [Description("Region (required)")] string region,
        [Description("Promo type: 'discount', 'bogo', 'display', 'digital', 'bundle'")] string promoType,
        [Description("Planned spend in dollars")] double spend,
        [Description("Duration in weeks (1-12)")] int durationWeeks,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string url = $"/api/promo/estimate-roi?brand={Uri.EscapeDataString(brand)}&region={Uri.EscapeDataString(region)}&promoType={Uri.EscapeDataString(promoType)}&spend={spend}&durationWeeks={durationWeeks}";
            HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "EstimateROITool failed for {Brand}/{Region}/{PromoType} — returning fallback", brand, region, promoType);
            return JsonSerializer.Serialize(new { brand, region, promo_type = promoType, spend, duration_weeks = durationWeeks, error = "ROI estimation unavailable — MCP server not reachable." });
        }
    }
}

using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

public class PromoHistoryTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PromoHistoryTool>? _logger;

    public PromoHistoryTool(HttpClient httpClient, ILogger<PromoHistoryTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Get historical promotion campaigns with outcomes including spend, lift, ROI, and success rating. Filterable by brand, region, and promo type.")]
    public async Task<string> GetPromoHistory(
        [Description("Brand name to filter. Omit for all brands.")] string? brand = null,
        [Description("Region to filter. Omit for all regions.")] string? region = null,
        [Description("Promo type ('discount', 'bogo', 'display', 'digital', 'bundle'). Omit for all.")] string? promoType = null,
        [Description("Months of history (1-24). Default: 18")] int months = 18,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string url = $"/api/promo/history?months={months}";
            if (!string.IsNullOrWhiteSpace(brand)) url += $"&brand={Uri.EscapeDataString(brand)}";
            if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";
            if (!string.IsNullOrWhiteSpace(promoType)) url += $"&promoType={Uri.EscapeDataString(promoType)}";

            HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "PromoHistoryTool failed — returning fallback");
            return JsonSerializer.Serialize(new { error = "Promo history unavailable — MCP server not reachable.", campaigns = Array.Empty<object>() });
        }
    }
}

using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

public class MarketShareTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MarketShareTool>? _logger;

    public MarketShareTool(HttpClient httpClient, ILogger<MarketShareTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Get market share trends over time. Returns quarterly share data with period-over-period changes and identifies significant share losses.")]
    public async Task<string> GetMarketShare(
        [Description("Brand name to filter. Omit for all brands.")] string? brand = null,
        [Description("Category to filter. Omit for all categories.")] string? category = null,
        [Description("Region to filter. Omit for all regions.")] string? region = null,
        [Description("Period to filter (e.g. '2026-Q1'). Omit for all periods.")] string? period = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = "/api/competitive/market-share?";
            if (!string.IsNullOrWhiteSpace(brand)) url += $"&brand={Uri.EscapeDataString(brand)}";
            if (!string.IsNullOrWhiteSpace(category)) url += $"&category={Uri.EscapeDataString(category)}";
            if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";
            if (!string.IsNullOrWhiteSpace(period)) url += $"&period={Uri.EscapeDataString(period)}";

            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "MarketShareTool failed — returning fallback");
            return JsonSerializer.Serialize(new { error = "Market share data unavailable — MCP server not reachable.", share_data = Array.Empty<object>() });
        }
    }
}

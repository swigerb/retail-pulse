using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

public class PortfolioDepletionStatsTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PortfolioDepletionStatsTool>? _logger;

    public PortfolioDepletionStatsTool(HttpClient httpClient, ILogger<PortfolioDepletionStatsTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Get depletion statistics for MULTIPLE brands in a single call. Use this for portfolio-wide comparisons, category rollups, and cross-brand analysis instead of calling GetDepletionStats per brand. Supports optional category filter (e.g. 'Home Improvement') to scope to one tenant category, an optional explicit brand list to compare a small set (e.g. 'Foundry Home,Urban Living'), and 'All'/'AllRegions' region values to fan the region axis out over every configured region in one call.")]
    public async Task<string> GetPortfolioDepletionStats(
        [Description("Region (e.g. 'Northeast', 'West Coast', 'National', or 'AllRegions' to return per-brand rows for every tenant region in one call).")] string region,
        [Description("Period (e.g. 'YTD', 'Q1', 'Q2')")] string period = "YTD",
        [Description("Optional category filter (e.g. 'Home Improvement', 'Spirits', 'Furniture'). When set, only brands in this tenant category are returned. Omit for all categories.")] string? category = null,
        [Description("Optional comma-separated brand list to restrict the answer to a specific comparison set (e.g. 'Foundry Home,Urban Living'). Omit to return every brand allowed by the category filter.")] string? brands = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = new List<string>
            {
                $"region={Uri.EscapeDataString(region)}",
                $"period={Uri.EscapeDataString(period)}",
            };
            if (!string.IsNullOrWhiteSpace(category))
                query.Add($"category={Uri.EscapeDataString(category)}");
            if (!string.IsNullOrWhiteSpace(brands))
                query.Add($"brands={Uri.EscapeDataString(brands)}");

            HttpResponseMessage response = await _httpClient.GetAsync(
                $"/api/portfolio-depletion-stats?{string.Join("&", query)}",
                cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "PortfolioDepletionStatsTool failed for {Region}/{Period}/cat={Category}/brands={Brands} — returning fallback", region, period, category, brands);
            return JsonSerializer.Serialize(new
            {
                region,
                period,
                category,
                brands,
                brandCount = 0,
                error = "Portfolio depletion data unavailable — MCP server not reachable.",
                source = "fallback"
            });
        }
    }
}

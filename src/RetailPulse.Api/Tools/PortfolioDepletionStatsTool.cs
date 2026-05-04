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

    [Description("Get depletion statistics for ALL brands in the portfolio in a single call. Use this for portfolio-wide comparisons, rankings, and cross-brand analysis instead of calling GetDepletionStats per brand.")]
    public async Task<string> GetPortfolioDepletionStats(
        [Description("Region (e.g. 'Northeast', 'West Coast', 'National')")] string region,
        [Description("Period (e.g. 'YTD', 'Q1', 'Q2')")] string period = "YTD",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"/api/portfolio-depletion-stats?region={Uri.EscapeDataString(region)}&period={Uri.EscapeDataString(period)}",
                cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "PortfolioDepletionStatsTool failed for {Region}/{Period} — returning fallback", region, period);
            return JsonSerializer.Serialize(new
            {
                region,
                period,
                brandCount = 0,
                brands = Array.Empty<object>(),
                error = "Portfolio depletion data unavailable — MCP server not reachable.",
                source = "fallback"
            });
        }
    }
}

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
        [Description("Optional region (e.g. 'Northeast', 'West Coast'). Omit it for a portfolio-wide ranking or comparison — the result is then the National aggregate.")] string? region = null,
        [Description("Period (e.g. 'YTD', 'Q1', 'Q2')")] string period = "YTD",
        CancellationToken cancellationToken = default)
    {
        // A portfolio-wide ranking has no region. Declaring region required meant the
        // model could not call this tool at all for "rank all brands by growth rate" —
        // the invocation failed before reaching the server, every brand came back
        // missing, and the coverage guard refused to draw the chart.
        string effectiveRegion = string.IsNullOrWhiteSpace(region) ? "National" : region;

        try
        {
            HttpResponseMessage response = await _httpClient.GetAsync(
                $"/api/portfolio-depletion-stats?region={Uri.EscapeDataString(effectiveRegion)}&period={Uri.EscapeDataString(period)}",
                cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "PortfolioDepletionStatsTool failed for {Region}/{Period} — returning fallback", effectiveRegion, period);
            return JsonSerializer.Serialize(new
            {
                region = effectiveRegion,
                period,
                brandCount = 0,
                brands = Array.Empty<object>(),
                error = "Portfolio depletion data unavailable — MCP server not reachable.",
                source = "fallback"
            });
        }
    }
}

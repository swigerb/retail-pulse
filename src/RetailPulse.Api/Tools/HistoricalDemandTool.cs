using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

[Obsolete("Use MCP demand tool instead. Will be removed in v2.")]
public class HistoricalDemandTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HistoricalDemandTool>? _logger;

    public HistoricalDemandTool(HttpClient httpClient, ILogger<HistoricalDemandTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Get historical depletion/demand data for a brand with an explicit dataset-relative period. Returns weekly source data that is compacted into bounded temporal and regional summaries.")]
    public async Task<string> GetHistoricalDemand(
        [Description("The brand name, e.g. 'Sierra Gold Tequila'")] string brand,
        [Description("The region, e.g. 'Northeast', 'National'. Defaults to 'National'.")] string region = "National",
        [Description("The channel, e.g. 'On-Premise', 'Off-Premise', 'E-Commerce', 'All'. Defaults to 'All'.")] string channel = "All",
        [Description("Supported period: current-month, current-quarter, current-year, or last-N-months where N is 1..24. Use current-quarter for 'this quarter'. Never substitute a broader period.")] string period = "last-12-months",
        CancellationToken cancellationToken = default)
    {
        if (!HistoricalDemandPeriodResolver.IsSupportedToolPeriod(period))
        {
            return JsonSerializer.Serialize(new
            {
                brand,
                region,
                channel,
                period,
                error = $"Unsupported historical-demand period '{period}'. No default period was substituted.",
                supported_periods = new[]
                {
                    "current-month", "current-quarter", "current-year", "last-N-months (N = 1..24)"
                }
            });
        }

        try
        {
            string url = $"/api/demand/history?brand={Uri.EscapeDataString(brand)}"
                + $"&period={Uri.EscapeDataString(period)}";
            if (!string.Equals(region, "National", StringComparison.OrdinalIgnoreCase))
            {
                url += $"&region={Uri.EscapeDataString(region)}";
            }
            if (!string.Equals(channel, "All", StringComparison.OrdinalIgnoreCase))
            {
                url += $"&channel={Uri.EscapeDataString(channel)}";
            }
            HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "HistoricalDemandTool failed for {Brand}/{Region}/{Channel} — returning fallback", brand, region, channel);
            return JsonSerializer.Serialize(new
            {
                brand,
                region,
                channel,
                period,
                error = "Historical demand data unavailable — MCP server not reachable.",
                monthly_data = Array.Empty<object>()
            });
        }
    }
}

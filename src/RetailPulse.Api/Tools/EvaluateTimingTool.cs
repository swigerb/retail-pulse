using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

public class EvaluateTimingTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<EvaluateTimingTool>? _logger;

    public EvaluateTimingTool(HttpClient httpClient, ILogger<EvaluateTimingTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Evaluate the timing of a proposed promotion. Checks for overlapping campaigns, seasonality fit, and cannibalization risk.")]
    public async Task<string> EvaluateTiming(
        [Description("Brand name (required)")] string brand,
        [Description("Region (required)")] string region,
        [Description("Proposed start date (ISO format, e.g. '2026-06-01')")] string startDate,
        [Description("Proposed end date (ISO format, e.g. '2026-06-28')")] string endDate,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string url = $"/api/promo/evaluate-timing?brand={Uri.EscapeDataString(brand)}&region={Uri.EscapeDataString(region)}&startDate={Uri.EscapeDataString(startDate)}&endDate={Uri.EscapeDataString(endDate)}";
            HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "EvaluateTimingTool failed for {Brand}/{Region} — returning fallback", brand, region);
            return JsonSerializer.Serialize(new { brand, region, start_date = startDate, end_date = endDate, error = "Timing evaluation unavailable — MCP server not reachable." });
        }
    }
}

using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

public class FieldSentimentTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<FieldSentimentTool>? _logger;

    public FieldSentimentTool(HttpClient httpClient, ILogger<FieldSentimentTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Get field sentiment and distributor feedback for a company brand in a specific region. Returns qualitative market intelligence from distributors and retailers.")]
    public async Task<string> GetFieldSentiment(
        [Description("The brand name, e.g. 'Sierra Gold Tequila', 'Ridgeline Bourbon'")] string brand,
        [Description("The region, e.g. 'Florida', 'Texas', 'California'")] string region,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"/api/field-sentiment?brand={Uri.EscapeDataString(brand)}&region={Uri.EscapeDataString(region)}",
                cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "FieldSentimentTool failed for brand {Brand}/{Region} — returning fallback", brand, region);
            return JsonSerializer.Serialize(new
            {
                brand,
                region,
                sentiment = "Data unavailable — MCP server not reachable.",
                source = "fallback"
            });
        }
    }
}

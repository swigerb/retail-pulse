using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

public class FieldSentimentTool
{
    private readonly HttpClient _httpClient;

    public FieldSentimentTool(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    [Description("Get field sentiment and distributor feedback for a Bacardi brand in a specific region. Returns qualitative market intelligence from distributors and retailers.")]
    public async Task<string> GetFieldSentiment(
        [Description("The brand name, e.g. 'Patron Silver', 'Angel's Envy'")] string brand,
        [Description("The region, e.g. 'Florida', 'Texas', 'California'")] string region)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"/api/field-sentiment?brand={Uri.EscapeDataString(brand)}&region={Uri.EscapeDataString(region)}");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch
        {
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

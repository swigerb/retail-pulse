using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

public class DepletionStatsTool
{
    private readonly HttpClient _httpClient;

    public DepletionStatsTool(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    [Description("Get depletion statistics for a Bacardi brand in a specific region and time period. Returns sales velocity, year-over-year trends, inventory levels, and stock status.")]
    public async Task<string> GetDepletionStats(
        [Description("The brand name, e.g. 'Patron Silver', 'Bacardi Superior', 'Grey Goose'")] string brand,
        [Description("The region, e.g. 'Florida', 'Texas', 'California', 'National'")] string region,
        [Description("The time period, e.g. 'YTD', 'Q1', 'Q2', 'Last12Months'")] string period)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"/api/depletion-stats?brand={Uri.EscapeDataString(brand)}&region={Uri.EscapeDataString(region)}&period={Uri.EscapeDataString(period)}");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch
        {
            // Fallback to simulated data
            return JsonSerializer.Serialize(new
            {
                brand,
                region,
                period,
                metrics = new
                {
                    depletions_yoy = "+2.1%",
                    sell_through_yoy = "-4.0%",
                    inventory_weeks_on_hand = 8.5,
                    status = "Overstocked"
                },
                sentiment_summary = "Data unavailable — MCP server not reachable."
            });
        }
    }
}

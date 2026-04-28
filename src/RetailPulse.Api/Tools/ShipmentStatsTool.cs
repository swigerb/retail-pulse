using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

public class ShipmentStatsTool
{
    private readonly HttpClient _httpClient;

    public ShipmentStatsTool(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    [Description("Get shipment statistics (sell-in from supplier to distributors) for a brand in a specific region. Returns shipment volumes, sell-through correlation, inventory levels, and pipeline anomaly detection (e.g., pipeline clogs where shipments outpace consumer demand).")]
    public async Task<string> GetShipmentStats(
        [Description("The brand name, e.g. 'Patron Silver', 'Grey Goose', 'Angel's Envy'")] string brand,
        [Description("The region, e.g. 'Florida', 'Texas', 'California', 'National'")] string region,
        [Description("The time period, e.g. 'YTD', 'Q1', 'Q2', 'Last12Months'")] string period = "YTD")
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"/api/shipment-stats?brand={Uri.EscapeDataString(brand)}&region={Uri.EscapeDataString(region)}&period={Uri.EscapeDataString(period)}");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch
        {
            return JsonSerializer.Serialize(new
            {
                brand,
                region,
                period,
                error = "Shipment data unavailable — MCP server not reachable.",
                source = "fallback"
            });
        }
    }
}

using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

public class LocalShipmentAnalyzer
{
    private readonly ShipmentStatsTool _shipmentTool;
    private readonly ILogger<LocalShipmentAnalyzer> _logger;

    public LocalShipmentAnalyzer(ShipmentStatsTool shipmentTool, ILogger<LocalShipmentAnalyzer> logger)
    {
        _shipmentTool = shipmentTool;
        _logger = logger;
    }

    [Description("Analyze shipment and Three-Tier distribution data for a brand. Fetches shipment statistics and returns detailed data for analysis including pipeline dynamics, anomaly detection (e.g., pipeline clogs where shipments outpace consumer demand), and sell-in vs sell-through correlation. Use this when the user asks about shipments, pipeline health, or Three-Tier distribution issues.")]
    public async Task<string> AnalyzeShipments(
        [Description("The brand to analyze, e.g. 'brand name'")] string brand,
        [Description("The region to analyze, e.g. 'Northeast', 'West Coast', 'National'")] string region,
        [Description("The time period, e.g. 'YTD', 'Q1'")] string period = "YTD")
    {
        _logger.LogInformation("Local shipment analysis for {Brand} in {Region} ({Period})", brand, region, period);

        var shipmentData = await _shipmentTool.GetShipmentStats(brand, region, period);

        return JsonSerializer.Serialize(new
        {
            source = "local-analyzer",
            brand,
            region,
            period,
            raw_shipment_data = shipmentData,
            instructions = "Analyze the raw shipment data above. Look for: pipeline clogs (shipments UP but sell-through DOWN), inventory buildups, velocity mismatches, and Three-Tier distribution tensions. Provide specific actionable recommendations."
        });
    }
}

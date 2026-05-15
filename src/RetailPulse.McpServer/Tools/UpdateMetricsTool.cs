using System.ComponentModel;
using ModelContextProtocol.Server;
using RetailPulse.McpServer.Data;

namespace RetailPulse.McpServer.Tools;

[McpServerToolType]
public static class UpdateMetricsTool
{
    [McpServerTool(Name = "UpdateMetrics")]
    [Description("Update a metric value for a specific brand and region. Use this to adjust depletion stats, shipment data, or sentiment when new information is received from the field.")]
    public static object UpdateMetrics(
        RetailPulseDb db,
        [Description("Table to update: 'Depletions', 'Shipments', or 'Sentiment'")] string table,
        [Description("Brand name (e.g. 'Sierra Gold Tequila')")] string brand,
        [Description("Region (e.g. 'Northeast', 'Midwest')")] string region,
        [Description("Field to update (e.g. 'DepletionsYoY', 'Status', 'Sentiment', 'CasesShipped')")] string field,
        [Description("New value for the field")] string value)
    {
        if (string.IsNullOrWhiteSpace(table))
            return new { error = "Parameter 'table' is required.", valid_tables = new[] { "Depletions", "Shipments", "Sentiment" } };
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required." };
        if (string.IsNullOrWhiteSpace(region))
            return new { error = "Parameter 'region' is required." };
        if (string.IsNullOrWhiteSpace(field))
            return new { error = "Parameter 'field' is required." };
        return string.IsNullOrWhiteSpace(value)
            ? (new { error = "Parameter 'value' is required." })
            : db.UpdateMetric(table, brand, region, field, value);
    }
}

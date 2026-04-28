using System.ComponentModel;
using System.Text.Json;
using RetailPulse.Contracts;

namespace RetailPulse.Api.Tools;

public class ChartDataTool
{
    private readonly ILogger<ChartDataTool> _logger;

    public ChartDataTool(ILogger<ChartDataTool> logger)
    {
        _logger = logger;
    }

    [Description("Create a chart visualization by providing structured chart data. Call this tool when you want to display a chart to the user. Provide the chart specification as a JSON string matching the ChartSpec schema. Supported chart types: line, bar, groupedBar, pie, donut, horizontalBar, stackedBar, gauge, table. Example: {\"type\":\"bar\",\"title\":\"Monthly Sales\",\"xAxisTitle\":\"Month\",\"yAxisTitle\":\"Cases\",\"data\":[{\"legend\":\"Patron Silver\",\"color\":\"#8B4513\",\"values\":[{\"x\":\"Jan\",\"y\":1200},{\"x\":\"Feb\",\"y\":1450}]}]}")]
    public Task<string> CreateChart(
        [Description("JSON string matching the ChartSpec schema with type, title, axis titles, and data series")] string chartSpecJson)
    {
        try
        {
            var spec = JsonSerializer.Deserialize<ChartSpec>(chartSpecJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (spec == null)
            {
                return Task.FromResult(JsonSerializer.Serialize(new { error = "Invalid chart specification" }));
            }

            _logger.LogInformation("Chart created: {Type} - {Title} with {SeriesCount} series", spec.Type, spec.Title, spec.Data.Count);

            return Task.FromResult(JsonSerializer.Serialize(new { status = "success", chart = spec }));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid chart spec JSON: {Json}", chartSpecJson);
            return Task.FromResult(JsonSerializer.Serialize(new { error = "Invalid JSON format", message = ex.Message }));
        }
    }
}

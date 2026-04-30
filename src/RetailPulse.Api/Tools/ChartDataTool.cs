using System.ComponentModel;
using System.Text.Json;
using RetailPulse.Contracts;

namespace RetailPulse.Api.Tools;

public class ChartDataTool
{
    private static readonly JsonSerializerOptions InputOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<ChartDataTool> _logger;

    public ChartDataTool(ILogger<ChartDataTool> logger)
    {
        _logger = logger;
    }

    [Description("Create a chart visualization by providing structured chart data. Provide JSON matching ChartSpec schema. Types: line, bar, groupedBar, pie, donut, horizontalBar, stackedBar, gauge, table.")]
    public Task<string> CreateChart(
        [Description("JSON string matching the ChartSpec schema")] string chartSpecJson)
    {
        try
        {
            var spec = JsonSerializer.Deserialize<ChartSpec>(chartSpecJson, InputOptions);
            if (spec == null)
            {
                return Task.FromResult(JsonSerializer.Serialize(new { error = "Invalid chart specification" }));
            }

            _logger.LogInformation("Chart created: {Type} - {Title} with {SeriesCount} series", spec.Type, spec.Title, spec.Data.Count);

            return Task.FromResult(JsonSerializer.Serialize(new { status = "success", chart = spec }));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid chart spec JSON");
            return Task.FromResult(JsonSerializer.Serialize(new { error = "Invalid JSON format", message = ex.Message }));
        }
    }
}

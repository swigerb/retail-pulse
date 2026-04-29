using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Tools;

namespace RetailPulse.Tests;

public class ChartDataToolTests
{
    private static ChartDataTool CreateTool() =>
        new(NullLogger<ChartDataTool>.Instance);

    [Fact]
    public async Task CreateChart_ValidJson_ReturnsSuccess()
    {
        var tool = CreateTool();
        var spec = """
            {
                "type": "bar",
                "title": "Monthly Sales",
                "xAxisTitle": "Month",
                "yAxisTitle": "Cases",
                "data": [
                    {
                        "legend": "Sierra Gold Tequila",
                        "color": "#1B4D7A",
                        "values": [
                            {"x": "Jan", "y": 1200},
                            {"x": "Feb", "y": 1450}
                        ]
                    }
                ]
            }
            """;

        var result = await tool.CreateChart(spec);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        var chart = doc.RootElement.GetProperty("chart");
        chart.GetProperty("Type").GetString().Should().Be("bar");
        chart.GetProperty("Title").GetString().Should().Be("Monthly Sales");
        chart.GetProperty("Data").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task CreateChart_InvalidJson_ReturnsStructuredError()
    {
        var tool = CreateTool();

        var result = await tool.CreateChart("{ this is : not valid json");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty("error", out var err).Should().BeTrue();
        err.GetString().Should().NotBeNullOrEmpty();
        doc.RootElement.GetProperty("message").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateChart_MissingRequiredFields_ReturnsError()
    {
        var tool = CreateTool();
        // Missing required "type" and "title" fields
        var result = await tool.CreateChart("""{"data":[]}""");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty("error", out _).Should().BeTrue(
            "ChartSpec requires type and title; deserialization should fail");
    }

    [Fact]
    public async Task CreateChart_EmptyDataArray_StillSucceeds()
    {
        var tool = CreateTool();
        var spec = """{"type":"bar","title":"Empty","data":[]}""";

        var result = await tool.CreateChart(spec);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        doc.RootElement.GetProperty("chart").GetProperty("Data").GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("LINE")]
    [InlineData("Line")]
    [InlineData("line")]
    public async Task CreateChart_TypePropertyIsCaseInsensitive(string typeValue)
    {
        var tool = CreateTool();
        var spec = $$"""
            {
                "TYPE": "{{typeValue}}",
                "TITLE": "Mixed Case Keys",
                "data": [
                    {"legend": "S1", "values": [{"x": "a", "y": 1}]}
                ]
            }
            """;

        var result = await tool.CreateChart(spec);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success",
            "PropertyNameCaseInsensitive should bind TYPE/TITLE keys");
        doc.RootElement.GetProperty("chart").GetProperty("Type").GetString().Should().Be(typeValue);
    }

    [Fact]
    public async Task CreateChart_NullLiteralJson_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.CreateChart("null");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty("error", out _).Should().BeTrue();
    }
}

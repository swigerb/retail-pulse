using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Tools;
using RetailPulse.Contracts;

namespace RetailPulse.Tests;

public class ChartDataToolTests
{
    private static ChartDataTool CreateTool() =>
        new(NullLogger<ChartDataTool>.Instance);

    [Fact]
    public async Task CreateChart_ValidJson_ReturnsSuccess()
    {
        ChartDataTool tool = CreateTool();
        string spec = /*lang=json,strict*/ """
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

        string result = await tool.CreateChart(spec);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        JsonElement chart = doc.RootElement.GetProperty("chart");
        chart.GetProperty("Type").GetString().Should().Be("bar");
        chart.GetProperty("Title").GetString().Should().Be("Monthly Sales");
        chart.GetProperty("Data").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task CreateChart_InvalidJson_ReturnsStructuredError()
    {
        ChartDataTool tool = CreateTool();

        string result = await tool.CreateChart("{ this is : not valid json");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty("error", out JsonElement err).Should().BeTrue();
        err.GetString().Should().NotBeNullOrEmpty();
        doc.RootElement.GetProperty("message").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateChart_MissingRequiredFields_ReturnsError()
    {
        ChartDataTool tool = CreateTool();
        // Missing required "type" and "title" fields
        string result = await tool.CreateChart(/*lang=json,strict*/ """{"data":[]}""");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty("error", out _).Should().BeTrue(
            "ChartSpec requires type and title; deserialization should fail");
    }

    [Fact]
    public async Task CreateChart_EmptyDataArray_StillSucceeds()
    {
        ChartDataTool tool = CreateTool();
        string spec = /*lang=json,strict*/ """{"type":"bar","title":"Empty","data":[]}""";

        string result = await tool.CreateChart(spec);

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
        ChartDataTool tool = CreateTool();
        string spec = $$"""
            {
                "TYPE": "{{typeValue}}",
                "TITLE": "Mixed Case Keys",
                "data": [
                    {"legend": "S1", "values": [{"x": "a", "y": 1}]}
                ]
            }
            """;

        string result = await tool.CreateChart(spec);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success",
            "PropertyNameCaseInsensitive should bind TYPE/TITLE keys");
        doc.RootElement.GetProperty("chart").GetProperty("Type").GetString().Should().Be(typeValue);
    }

    [Fact]
    public async Task CreateChart_NullLiteralJson_ReturnsError()
    {
        ChartDataTool tool = CreateTool();

        string result = await tool.CreateChart("null");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty("error", out _).Should().BeTrue();
    }

    [Fact]
    public async Task CreateChart_ValidJson_ReportsNotRecovered()
    {
        ChartDataTool tool = CreateTool();
        string spec = /*lang=json,strict*/ """{"type":"bar","title":"Ok","data":[{"legend":"S1","values":[{"x":"a","y":1}]}]}""";

        string result = await tool.CreateChart(spec);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        doc.RootElement.GetProperty("recovered").GetBoolean().Should().BeFalse(
            "a strictly valid payload is not a recovery");
    }

    [Fact]
    public async Task CreateChart_UnterminatedObject_RecoversLeadingData()
    {
        ChartDataTool tool = CreateTool();
        // Truncated mid-second-datapoint: the container is never closed.
        string truncated =
            """{"type":"bar","title":"Depletion Velocity","xAxisTitle":"Brand","data":[{"legend":"ClearDesk","color":"#1B4D7A","values":[{"x":"Northeast","y":12.5},{"x":"Nor""";

        string result = await tool.CreateChart(truncated);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        doc.RootElement.GetProperty("recovered").GetBoolean().Should().BeTrue();
        JsonElement chart = doc.RootElement.GetProperty("chart");
        chart.GetProperty("Type").GetString().Should().Be("bar");
        chart.GetProperty("Title").GetString().Should().Be("Depletion Velocity");
        JsonElement series = chart.GetProperty("Data");
        series.GetArrayLength().Should().Be(1);
        JsonElement values = series[0].GetProperty("Values");
        values.GetArrayLength().Should().Be(1, "only the complete leading datapoint is salvaged");
        values[0].GetProperty("X").GetString().Should().Be("Northeast");
        values[0].GetProperty("Y").GetDouble().Should().Be(12.5);
    }

    [Fact]
    public async Task CreateChart_ArrayTruncatedMidDatapoint_DropsIncompletePoint()
    {
        ChartDataTool tool = CreateTool();
        // Second datapoint has an x but the y value is cut off.
        string truncated =
            """{"type":"line","title":"Trend","data":[{"legend":"BrandA","values":[{"x":"Jan","y":10},{"x":"Feb","y":""";

        string result = await tool.CreateChart(truncated);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        doc.RootElement.GetProperty("recovered").GetBoolean().Should().BeTrue();
        JsonElement values = doc.RootElement.GetProperty("chart").GetProperty("Data")[0].GetProperty("Values");
        values.GetArrayLength().Should().Be(1, "the datapoint missing y must be dropped, not fabricated");
        values[0].GetProperty("X").GetString().Should().Be("Jan");
    }

    [Fact]
    public async Task CreateChart_TruncatedString_TitleCutOff_ReturnsError()
    {
        ChartDataTool tool = CreateTool();
        // The title string is truncated before any usable data exists.
        string truncated = """{"type":"bar","title":"Depletion Veloc""";

        string result = await tool.CreateChart(truncated);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty("error", out _).Should().BeTrue(
            "no usable chart data could be recovered");
        doc.RootElement.GetProperty("recovered").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task CreateChart_TruncatedStringMidDatapoint_RecoversPriorPoints()
    {
        ChartDataTool tool = CreateTool();
        // The x label of the last datapoint is a truncated string.
        string truncated =
            """{"type":"bar","title":"Velocity","data":[{"legend":"BrandA","values":[{"x":"NE","y":5},{"x":"Mid""";

        string result = await tool.CreateChart(truncated);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        doc.RootElement.GetProperty("recovered").GetBoolean().Should().BeTrue();
        JsonElement values = doc.RootElement.GetProperty("chart").GetProperty("Data")[0].GetProperty("Values");
        values.GetArrayLength().Should().Be(1);
        values[0].GetProperty("X").GetString().Should().Be("NE");
    }

    [Fact]
    public async Task CreateChart_FencedTruncatedJson_RecoversData()
    {
        ChartDataTool tool = CreateTool();
        // Model wrapped output in a markdown fence and got cut off (no closing fence).
        string truncated =
            "```json\n" +
            """{"type":"bar","title":"Spirits NE","data":[{"legend":"ClearDesk","values":[{"x":"NE","y":8.3}""";

        string result = await tool.CreateChart(truncated);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        doc.RootElement.GetProperty("recovered").GetBoolean().Should().BeTrue();
        JsonElement chart = doc.RootElement.GetProperty("chart");
        chart.GetProperty("Title").GetString().Should().Be("Spirits NE");
        chart.GetProperty("Data")[0].GetProperty("Values")[0].GetProperty("Y").GetDouble().Should().Be(8.3);
    }

    [Fact]
#pragma warning disable JSON002 // Deliberately wrapped in markdown fences.
    public async Task CreateChart_FencedValidJson_Succeeds()
    {
        ChartDataTool tool = CreateTool();
        string fenced =
            "```json\n" +
            /*lang=json,strict*/ """{"type":"bar","title":"Fenced","data":[{"legend":"S1","values":[{"x":"a","y":1}]}]}""" +
            "\n```";

        string result = await tool.CreateChart(fenced);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        doc.RootElement.GetProperty("recovered").GetBoolean().Should().BeTrue(
            "fenced JSON is not strictly valid input, so it goes through the recovery path");
        doc.RootElement.GetProperty("chart").GetProperty("Data")[0]
            .GetProperty("Values").GetArrayLength().Should().Be(1);
    }
#pragma warning restore JSON002

    [Fact]
    public async Task CreateChart_TruncatedBeforeAnyDatapoint_ReturnsError()
    {
        ChartDataTool tool = CreateTool();
        // Structurally recoverable to {"type":"bar","title":"X","data":[]} but no data.
        string truncated = """{"type":"bar","title":"X","data":[{"legend":"BrandA","values":[{"x":"NE""";

        string result = await tool.CreateChart(truncated);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty("error", out _).Should().BeTrue(
            "a datapoint with no y is not usable and there is no other data");
        doc.RootElement.GetProperty("recovered").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task CreateChart_GarbageAfterBrace_ReturnsError()
    {
        ChartDataTool tool = CreateTool();

        string result = await tool.CreateChart("{ not : valid");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty("error", out _).Should().BeTrue();
        doc.RootElement.GetProperty("message").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateChart_RecoveredData_FlowsThroughChartExtraction()
    {
        // End-to-end contract check: a recovered payload must still match the shape
        // that AgentExecutionPipeline.ExtractChartSpecs consumes (status=success + chart).
        ChartDataTool tool = CreateTool();
        string truncated =
            """{"type":"bar","title":"Spirits","data":[{"legend":"ClearDesk","values":[{"x":"NE","y":8.3}""";

        string result = await tool.CreateChart(truncated);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        JsonElement chartElement = doc.RootElement.GetProperty("chart");
        ChartSpec? chart = JsonSerializer.Deserialize<ChartSpec>(
            chartElement.GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        chart.Should().NotBeNull();
        chart.Type.Should().Be("bar");
        chart.Data.Should().ContainSingle();
        chart.Data[0].Values.Should().ContainSingle();
    }

    [Fact]
    public async Task ExtractChartSpecs_RecoveredTruncatedChart_FlowsIntoChartsList()
    {
        // Full cross-boundary contract: the recovered CreateChart payload must be
        // consumed by the REAL AgentExecutionPipeline.ExtractChartSpecs and land as a
        // single ChartSpec in the response Charts list (not re-implemented inline).
        ChartDataTool tool = CreateTool();
        string truncated =
            """{"type":"bar","title":"Spirits NE","data":[{"legend":"ClearDesk","values":[{"x":"NE","y":8.3}""";

        string toolOutput = await tool.CreateChart(truncated);

        var response = new Microsoft.Extensions.AI.ChatResponse(
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-chart-1", toolOutput)]));

        List<ChartSpec> charts = AgentExecutionPipeline.ExtractChartSpecs(response);

        charts.Should().ContainSingle("the recovered singular chart must flow into the Charts list");
        charts[0].Type.Should().Be("bar");
        charts[0].Title.Should().Be("Spirits NE");
        charts[0].Data.Should().ContainSingle();
        charts[0].Data[0].Values.Should().ContainSingle();
        charts[0].Data[0].Values[0].X.Should().Be("NE");
        charts[0].Data[0].Values[0].Y.Should().Be(8.3);
    }

    [Fact]
    public async Task ExtractChartSpecs_StructuredError_ProducesNoCharts()
    {
        // A conservative, unrecoverable result must be a structured error and must NOT
        // be surfaced as a chart by the real consumer (charts=null, not a broken chart).
        ChartDataTool tool = CreateTool();
        string truncated = """{"type":"bar","title":"Depletion Veloc""";

        string toolOutput = await tool.CreateChart(truncated);
        JsonDocument.Parse(toolOutput).RootElement.TryGetProperty("error", out _).Should().BeTrue(
            "the payload is unrecoverable and must be a structured error");

        var response = new Microsoft.Extensions.AI.ChatResponse(
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-chart-2", toolOutput)]));

        List<ChartSpec> charts = AgentExecutionPipeline.ExtractChartSpecs(response);

        charts.Should().BeEmpty("an error result must not produce any chart");
    }

    [Fact]
    public async Task CreateChart_AlternateChartJsSchema_NormalizesToSuccess()
    {
        // Well-formed JSON but the model-invented Chart.js-style schema
        // (data:{labels,series}) that strict ChartSpec binding cannot handle.
        ChartDataTool tool = CreateTool();
        string spec = /*lang=json,strict*/ """
            {
                "type": "bar",
                "title": "Consolidation Check 2026-08-05",
                "data": {
                    "labels": ["ClearDesk Vodka", "Sierra Gold Tequila", "Apex Reserve"],
                    "series": [
                        {"name": "Depletion Velocity", "values": [12.5, 9.8, 7.2]}
                    ]
                },
                "options": {"orientation": "horizontal", "xAxisLabel": "Cases per Week", "yAxisLabel": "Brand"}
            }
            """;

        string result = await tool.CreateChart(spec);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        doc.RootElement.GetProperty("recovered").GetBoolean().Should().BeTrue(
            "an alternate-schema payload is bound via the normalizer, not strict deserialization");
        JsonElement chart = doc.RootElement.GetProperty("chart");
        chart.GetProperty("Type").GetString().Should().Be("horizontalBar");
        chart.GetProperty("Title").GetString().Should().Be("Consolidation Check 2026-08-05");
        JsonElement values = chart.GetProperty("Data")[0].GetProperty("Values");
        values.GetArrayLength().Should().Be(3);
        values[0].GetProperty("X").GetString().Should().Be("ClearDesk Vodka");
        values[0].GetProperty("Y").GetDouble().Should().Be(12.5);
    }

    [Fact]
    public async Task CreateChart_AlternateSchema_FlowsIntoChartsList()
    {
        // Cross-boundary contract: the normalized alternate-schema chart must land in
        // the real AgentExecutionPipeline.ExtractChartSpecs Charts list.
        ChartDataTool tool = CreateTool();
        string spec = /*lang=json,strict*/ """
            {"type":"line","title":"Trend","data":{"labels":["Jan","Feb"],"series":[{"name":"BrandA","values":[10,20]}]}}
            """;

        string toolOutput = await tool.CreateChart(spec);
        var response = new Microsoft.Extensions.AI.ChatResponse(
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-chart-3", toolOutput)]));

        List<ChartSpec> charts = AgentExecutionPipeline.ExtractChartSpecs(response);

        charts.Should().ContainSingle();
        charts[0].Type.Should().Be("line");
        charts[0].Data[0].Values.Should().HaveCount(2);
        charts[0].Data[0].Values[1].X.Should().Be("Feb");
        charts[0].Data[0].Values[1].Y.Should().Be(20);
    }
}

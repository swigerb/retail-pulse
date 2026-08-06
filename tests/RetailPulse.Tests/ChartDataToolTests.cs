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
    public async Task CreateChart_EmptyDataArray_ReturnsError()
    {
        // Invariant: an empty chart (recognized type + title but no series) is NOT
        // renderable and must be a structured diagnostic, never status:success — a
        // success-shaped empty chart is exactly what produced the blank card (issue #32).
        ChartDataTool tool = CreateTool();
        string spec = /*lang=json,strict*/ """{"type":"bar","title":"Empty","data":[]}""";

        string result = await tool.CreateChart(spec);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty("status", out _).Should().BeFalse(
            "an empty chart must not be reported as success");
        doc.RootElement.TryGetProperty("error", out JsonElement err).Should().BeTrue();
        err.GetString().Should().NotBeNullOrEmpty();
        doc.RootElement.GetProperty("recovered").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task CreateChart_SeriesWithNoValues_ReturnsError()
    {
        // Strict deserialization binds a legend-only series with zero datapoints
        // (Data.Count == 1) — structurally valid but unrenderable. It must be rejected.
        ChartDataTool tool = CreateTool();
        string spec = /*lang=json,strict*/ """{"type":"line","title":"No Points","data":[{"legend":"BrandA","values":[]}]}""";

        string result = await tool.CreateChart(spec);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty("status", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("error", out _).Should().BeTrue(
            "a series with no finite datapoints is not renderable");
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

    [Fact]
    public async Task CreateChart_FullConfigSchema_TopLevelSeriesAndXAxis_Normalizes()
    {
        // Third real-world variant observed live: top-level "series" with per-series
        // "data":[numbers], category labels under xAxis.categories, and axis titles
        // under xAxis.label / yAxis.label. No top-level "data" property at all.
        ChartDataTool tool = CreateTool();
        string spec = /*lang=json,strict*/ """
            {
                "type": "bar",
                "title": "Depletion Velocity for Spirits Brands in the Northeast",
                "xAxis": {"label": "Brand", "categories": ["Sierra Gold Tequila", "Ridgeline Bourbon", "Summit Vodka"]},
                "yAxis": {"label": "Avg Weekly Volume", "format": "number"},
                "series": [{"name": "Avg Weekly Volume", "data": [1893.2, 2109.5, 2296.3], "color": "#1B4D7A"}]
            }
            """;

        string result = await tool.CreateChart(spec);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        doc.RootElement.GetProperty("recovered").GetBoolean().Should().BeTrue();
        JsonElement chart = doc.RootElement.GetProperty("chart");
        chart.GetProperty("Type").GetString().Should().Be("bar");
        chart.GetProperty("XAxisTitle").GetString().Should().Be("Brand");
        chart.GetProperty("YAxisTitle").GetString().Should().Be("Avg Weekly Volume");
        JsonElement values = chart.GetProperty("Data")[0].GetProperty("Values");
        values.GetArrayLength().Should().Be(3);
        values[0].GetProperty("X").GetString().Should().Be("Sierra Gold Tequila");
        values[0].GetProperty("Y").GetDouble().Should().Be(1893.2);
        values[2].GetProperty("X").GetString().Should().Be("Summit Vodka");
        values[2].GetProperty("Y").GetDouble().Should().Be(2296.3);
    }

    // ---- Issue #32: two-brand / all-regions comparison must bind to two non-empty series ----

    [Fact]
    public async Task CreateChart_TwoBrandComparison_DataPointsSchema_BindsBothSeries()
    {
        // Exact live shape (issue #32): the Demand Forecast Agent emitted the
        // "Coastline Tacos vs Apex Grill ... Across All Regions" comparison as a
        // top-level series[] with per-series "dataPoints" and shared top-level
        // "categories". Strict ChartSpec binding leaves Data empty (no "data" key),
        // which previously returned success with an empty chart → blank card.
        ChartDataTool tool = CreateTool();
        string spec = /*lang=json,strict*/ """
            {
                "type": "groupedBar",
                "title": "Coastline Tacos vs Apex Grill: Weekly Depletion Trend Across All Regions",
                "xAxisTitle": "Region",
                "yAxisTitle": "Weekly Depletions (cases)",
                "categories": ["Northeast", "Southeast", "Midwest", "West", "Southwest"],
                "series": [
                    {"name": "Coastline Tacos", "dataPoints": [4200, 3800, 5100, 4700, 3300]},
                    {"name": "Apex Grill", "dataPoints": [3900, 4100, 4600, 5200, 3000]}
                ]
            }
            """;

        string result = await tool.CreateChart(spec);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        doc.RootElement.GetProperty("recovered").GetBoolean().Should().BeTrue();
        JsonElement chart = doc.RootElement.GetProperty("chart");
        chart.GetProperty("Type").GetString().Should().Be("groupedBar");
        JsonElement data = chart.GetProperty("Data");
        data.GetArrayLength().Should().Be(2, "both brands must bind as their own series");
        data[0].GetProperty("Legend").GetString().Should().Be("Coastline Tacos");
        data[1].GetProperty("Legend").GetString().Should().Be("Apex Grill");
        JsonElement s0 = data[0].GetProperty("Values");
        s0.GetArrayLength().Should().Be(5, "the five regional categories must align to the first series");
        s0[0].GetProperty("X").GetString().Should().Be("Northeast");
        s0[0].GetProperty("Y").GetDouble().Should().Be(4200);
        data[1].GetProperty("Values")[3].GetProperty("X").GetString().Should().Be("West");
        data[1].GetProperty("Values")[3].GetProperty("Y").GetDouble().Should().Be(5200);
    }

    [Fact]
    public async Task ExtractChartSpecs_TwoBrandComparison_FlowsBothSeriesIntoChartsList()
    {
        // Cross-boundary: the recovered two-series comparison must land in the real
        // AgentExecutionPipeline.ExtractChartSpecs Charts list with both legends intact.
        ChartDataTool tool = CreateTool();
        string spec = /*lang=json,strict*/ """
            {
                "type": "line",
                "title": "Coastline Tacos vs Apex Grill: Weekly Depletion Trend Across All Regions",
                "categories": ["Northeast", "Southeast", "Midwest"],
                "series": [
                    {"name": "Coastline Tacos", "dataPoints": [4200, 3800, 5100]},
                    {"name": "Apex Grill", "dataPoints": [3900, 4100, 4600]}
                ]
            }
            """;

        string toolOutput = await tool.CreateChart(spec);
        var response = new Microsoft.Extensions.AI.ChatResponse(
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-cmp-1", toolOutput)]));

        List<ChartSpec> charts = AgentExecutionPipeline.ExtractChartSpecs(response);

        charts.Should().ContainSingle();
        charts[0].Data.Should().HaveCount(2);
        charts[0].Data.Select(s => s.Legend).Should().Equal("Coastline Tacos", "Apex Grill");
        charts[0].Data.Should().OnlyContain(s => s.Values.Count == 3);
    }

    [Fact]
    public async Task CreateChart_CanonicalTwoSeriesComparison_Succeeds()
    {
        // The canonical schema the tightened prompt steers models toward: one entry per
        // series in "data", every series sharing the same "x" category labels.
        ChartDataTool tool = CreateTool();
        string spec = /*lang=json,strict*/ """
            {
                "type": "groupedBar",
                "title": "Coastline Tacos vs Apex Grill by Region",
                "data": [
                    {"legend": "Coastline Tacos", "values": [{"x": "Northeast", "y": 4200}, {"x": "West", "y": 4700}]},
                    {"legend": "Apex Grill", "values": [{"x": "Northeast", "y": 3900}, {"x": "West", "y": 5200}]}
                ]
            }
            """;

        string result = await tool.CreateChart(spec);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        doc.RootElement.GetProperty("recovered").GetBoolean().Should().BeFalse(
            "the canonical schema binds via strict deserialization, not recovery");
        doc.RootElement.GetProperty("chart").GetProperty("Data").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task CreateChart_UnevenCategoriesAndSeries_KeepsAlignmentByIndex()
    {
        // Mismatched lengths: 3 categories but a series with only 2 points, and another
        // with an extra 4th point. Points must stay aligned to the categories by index
        // and the extra point falls back to an index label — never fabricated or dropped
        // into the wrong category.
        ChartDataTool tool = CreateTool();
        string spec = /*lang=json,strict*/ """
            {
                "type": "line",
                "title": "Uneven Comparison",
                "categories": ["Q1", "Q2", "Q3"],
                "series": [
                    {"name": "BrandA", "values": [100, 200]},
                    {"name": "BrandB", "values": [110, 210, 310, 410]}
                ]
            }
            """;

        string result = await tool.CreateChart(spec);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        JsonElement data = doc.RootElement.GetProperty("chart").GetProperty("Data");
        data.GetArrayLength().Should().Be(2);
        JsonElement a = data[0].GetProperty("Values");
        a.GetArrayLength().Should().Be(2);
        a[0].GetProperty("X").GetString().Should().Be("Q1");
        a[1].GetProperty("X").GetString().Should().Be("Q2");
        JsonElement b = data[1].GetProperty("Values");
        b.GetArrayLength().Should().Be(4);
        b[2].GetProperty("X").GetString().Should().Be("Q3");
        b[3].GetProperty("X").GetString().Should().Be("4", "a point past the category list falls back to its 1-based index");
    }

    [Fact]
    public async Task CreateChart_NumericStringValues_Bind()
    {
        // Models sometimes stringify the y values. Numeric strings must bind.
        ChartDataTool tool = CreateTool();
        string spec = /*lang=json,strict*/ """
            {
                "type": "bar",
                "title": "Stringified Values",
                "categories": ["NE", "SE"],
                "series": [{"name": "BrandA", "dataPoints": ["1200.5", "980"]}]
            }
            """;

        string result = await tool.CreateChart(spec);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        JsonElement values = doc.RootElement.GetProperty("chart").GetProperty("Data")[0].GetProperty("Values");
        values[0].GetProperty("Y").GetDouble().Should().Be(1200.5);
        values[1].GetProperty("Y").GetDouble().Should().Be(980);
    }

    [Fact]
    public async Task CreateChart_NonFiniteValues_AreDroppedNotRendered()
    {
        // NaN / Infinity tokens are not bindable. A series whose only points are
        // non-finite is dropped; if that leaves nothing renderable, it is an error.
        ChartDataTool tool = CreateTool();
        string spec = /*lang=json,strict*/ """
            {
                "type": "line",
                "title": "Non-finite",
                "categories": ["A", "B"],
                "series": [{"name": "BrandA", "dataPoints": ["NaN", "Infinity"]}]
            }
            """;

        string result = await tool.CreateChart(spec);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty("status", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("error", out _).Should().BeTrue(
            "non-finite values are not renderable and leave no usable data");
    }

    [Fact]
    public async Task CreateChart_MixedFiniteAndNonFinite_KeepsFinitePoints()
    {
        ChartDataTool tool = CreateTool();
        string spec = /*lang=json,strict*/ """
            {
                "type": "line",
                "title": "Mixed",
                "categories": ["A", "B", "C"],
                "series": [{"name": "BrandA", "dataPoints": [10, "NaN", 30]}]
            }
            """;

        string result = await tool.CreateChart(spec);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        JsonElement values = doc.RootElement.GetProperty("chart").GetProperty("Data")[0].GetProperty("Values");
        values.GetArrayLength().Should().Be(2, "the NaN point is dropped, the finite points kept");
        values[0].GetProperty("Y").GetDouble().Should().Be(10);
        values[1].GetProperty("X").GetString().Should().Be("C");
        values[1].GetProperty("Y").GetDouble().Should().Be(30);
    }
}

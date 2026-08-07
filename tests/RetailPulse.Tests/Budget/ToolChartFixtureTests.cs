using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Budget;
using RetailPulse.Api.Tools;
using Xunit;

namespace RetailPulse.Tests.Budget;

/// <summary>
/// End-to-end fixture for the P0 query's chart: a two-brand (Coastline Tacos vs Apex
/// Grill) grouped-bar comparison across three regions must survive <c>CreateChart</c>
/// AND the budget boundary with exactly two series and six aligned marks — proving the
/// canonical ChartSpec the frontend renders is never compacted or truncated.
/// </summary>
public sealed class ToolChartFixtureTests
{
    private static ToolResultBudget CreateBudget() =>
        new([new HistoricalDemandCompactor(), new PortfolioDepletionCompactor()]);

    private static readonly ToolResultBudgetOptions _options = new()
    {
        Enabled = true,
        MaxResultChars = 6000,
        MaxCumulativeChars = 24_000,
        MaxToolCalls = 8,
        CharsPerToken = 4
    };

    private const string TwoBrandGroupedBar = /*lang=json,strict*/ """
    {
      "type": "groupedBar",
      "title": "Depletions: Coastline Tacos vs Apex Grill by Region",
      "xAxisTitle": "Region",
      "yAxisTitle": "Depletions",
      "data": [
        { "legend": "Coastline Tacos", "color": "#4C78A8", "values": [
          { "x": "West", "y": 1200.0 }, { "x": "Central", "y": 900.0 }, { "x": "East", "y": 1050.0 } ] },
        { "legend": "Apex Grill", "color": "#F58518", "values": [
          { "x": "West", "y": 1500.0 }, { "x": "Central", "y": 700.0 }, { "x": "East", "y": 1320.0 } ] }
      ]
    }
    """;

    [Fact]
    public async Task TwoBrandChart_HasTwoSeriesAndSixMarks_AndIsNotCompacted()
    {
        var chartTool = new ChartDataTool(NullLogger<ChartDataTool>.Instance);
        string createResult = await chartTool.CreateChart(TwoBrandGroupedBar);

        // 1) CreateChart itself must succeed with the canonical shape. The success
        // envelope uses lowercase anonymous keys (status/chart) while the nested
        // ChartSpec serializes with its PascalCase property names (Data/Values).
        using var created = JsonDocument.Parse(createResult);
        created.RootElement.GetProperty("status").GetString().Should().Be("success");
        JsonElement chart = created.RootElement.GetProperty("chart");
        JsonElement series = chart.GetProperty("Data");
        series.GetArrayLength().Should().Be(2, "two legends: Coastline Tacos and Apex Grill");

        int totalMarks = series.EnumerateArray().Sum(s => s.GetProperty("Values").GetArrayLength());
        totalMarks.Should().Be(6, "2 series x 3 regions = 6 bars");

        // 2) The budget boundary must treat CreateChart as exempt — byte-for-byte unchanged.
        ToolResultBudget budget = CreateBudget();
        BudgetedResult budgeted = budget.Apply("CreateChart", createResult, _options);
        budgeted.Json.Should().Be(createResult, "the canonical ChartSpec must never be compacted");
        budgeted.Metrics.Exempt.Should().BeTrue();
        budgeted.Metrics.Compacted.Should().BeFalse();
        budgeted.Metrics.Truncated.Should().BeFalse();

        // 3) Series/marks are still intact after passing through the boundary.
        using var afterDoc = JsonDocument.Parse(budgeted.Json);
        JsonElement afterSeries = afterDoc.RootElement.GetProperty("chart").GetProperty("Data");
        afterSeries.GetArrayLength().Should().Be(2);
        afterSeries.EnumerateArray().Sum(s => s.GetProperty("Values").GetArrayLength()).Should().Be(6);
    }
}

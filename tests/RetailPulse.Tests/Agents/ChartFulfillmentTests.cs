using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Charts;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;
using RetailPulse.Tests.Fixtures;
using Xunit;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;

namespace RetailPulse.Tests.Agents;

/// <summary>
/// Tests for the explicit chart-request detector, the deterministic (no-LLM) chart
/// builder, and the pipeline's chart-fulfillment invariant.
/// </summary>
public sealed class ChartFulfillmentTests
{
    // ── ChartRequestDetector ────────────────────────────────────────────────

    [Theory]
    // Exact P0 prompts.
    [InlineData("Show me a bar chart comparing depletion velocity for all spirits brands in the Northeast",
        "bar", AgentIntent.DemandForecasting)]
    [InlineData("Show a gauge chart for Pinnacle Hardware inventory health in the Midwest",
        "gauge", AgentIntent.SupplyShipments)]
    // Generic type + domain cues.
    [InlineData("line chart of gross margin by brand", "line", AgentIntent.MarginAnalysis)]
    [InlineData("pie chart of market share by competitor", "pie", AgentIntent.CompetitiveMarket)]
    [InlineData("donut chart of promotion spend", "donut", AgentIntent.PromotionTrade)]
    public void Detect_ExplicitChartRequest_ReturnsTypeAndSpecialist(
        string message, string expectedType, string expectedIntent)
    {
        ChartIntent intent = ChartRequestDetector.Detect(message);

        intent.IsExplicitChartRequest.Should().BeTrue();
        intent.ChartType.Should().Be(expectedType);
        intent.RoutedIntent.Should().Be(expectedIntent);
        intent.RoutedIntent.Should().NotBe(AgentIntent.PortfolioHealth,
            "explicit chart requests must never route to the health council");
    }

    [Theory]
    [InlineData("How is the portfolio performing?")]
    [InlineData("What's the inventory level for Pinnacle Hardware?")]
    [InlineData("Summarize distributor sentiment this quarter")]
    [InlineData("")]
    // Bare VERB uses of ambiguous type words must route normally — never a chart request.
    [InlineData("How would you gauge our portfolio's performance this quarter?")]
    [InlineData("gauge customer sentiment")]
    [InlineData("gauge the risk")]
    [InlineData("Can you gauge how the Northeast is trending?")]
    // Ordinary-language collisions for other type words must not trip the detector.
    [InlineData("Raise the bar for store performance next quarter")]
    [InlineData("Draw a line in the sand on trade spend")]
    [InlineData("Book a table for two at the distributor dinner")]
    [InlineData("What's the bottom line on margin this year?")]
    [InlineData("Table the discussion about planogram resets")]
    public void Detect_NonChartPrompt_IsNotExplicit(string message)
    {
        ChartIntent intent = ChartRequestDetector.Detect(message);
        intent.IsExplicitChartRequest.Should().BeFalse();
    }

    [Theory]
    // A chart-only type word used as a noun (no literal "chart") is still explicit.
    [InlineData("Show a gauge for Pinnacle Hardware inventory health", "gauge", AgentIntent.SupplyShipments)]
    [InlineData("create a gauge for stockout risk in the West", "gauge", AgentIntent.SupplyShipments)]
    [InlineData("render a gauge", "gauge", AgentIntent.General)]
    [InlineData("gauge for supply health in the West", "gauge", AgentIntent.SupplyShipments)]
    // "table" as a chart-object noun is only explicit when followed by a data cue that a
    // seating/verb use never carries — the curated home-improvement prompt qualifies.
    [InlineData("Create a table showing depletion stats for all home improvement brands by region",
        "table", AgentIntent.DemandForecasting)]
    [InlineData("Show me a table listing brand performance by region", "table", AgentIntent.General)]
    [InlineData("Generate a table comparing depletion trends across regions", "table", AgentIntent.DemandForecasting)]
    public void Detect_ChartOnlyTypeAsNoun_IsExplicit(
        string message, string expectedType, string expectedIntent)
    {
        ChartIntent intent = ChartRequestDetector.Detect(message);

        intent.IsExplicitChartRequest.Should().BeTrue();
        intent.ChartType.Should().Be(expectedType);
        intent.RoutedIntent.Should().Be(expectedIntent);
        intent.RoutedIntent.Should().NotBe(AgentIntent.PortfolioHealth);
    }

    [Fact]
    public void Detect_GaugeWord_IsAlwaysGauge()
    {
        ChartRequestDetector.Detect("gauge for supply health in the West")
            .ChartType.Should().Be("gauge");
    }

    [Fact]
    public void EnforceChartFulfillment_VerbGaugePortfolio_NoDiagnostic()
    {
        // "gauge" used as a verb about portfolio performance is NOT an explicit chart
        // request, so the fulfillment invariant must be a no-op — no chart, no diagnostic.
        AgentExecutionPipeline pipeline = CreatePipeline();
        MeaiChatResponse response = ResponseWithToolResults(
            JsonSerializer.Serialize(new { note = "council prose only" }));

        var charts = new List<ChartSpec>();
        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            "How would you gauge our portfolio's performance this quarter?",
            response, charts, "The council's synthesis: healthy overall.");

        result.Charts.Should().BeEmpty("a verb use of 'gauge' must never force a chart");
        result.Reply.Should().Be("The council's synthesis: healthy overall.");
        result.Reply.Should().NotContain("Chart unavailable");
    }

    // ── DeterministicChartBuilder ───────────────────────────────────────────

    [Fact]
    public void TryBuild_ThreeBrandDemandPayloads_BuildsBarWithThreeMarks()
    {
        MeaiChatResponse response = ResponseWithToolResults(
            CompactedDemand("Sierra Gold Tequila", "Northeast", avgWeekly: 820.0),
            CompactedDemand("Ridgeline Bourbon", "Northeast", avgWeekly: 640.0),
            CompactedDemand("Summit Vodka", "Northeast", avgWeekly: 910.0));

        bool built = DeterministicChartBuilder.TryBuild(response, "bar", out ChartSpec? chart);

        built.Should().BeTrue();
        chart!.Type.Should().Be("bar");
        chart.Title.Should().Contain("Northeast");
        chart.Data.Should().HaveCount(1);
        chart.Data[0].Values.Should().HaveCount(3, "three distinct brand series must not be deduped");
        chart.Data[0].Values.Select(v => v.X).Should().BeEquivalentTo(
            ["Sierra Gold Tequila", "Ridgeline Bourbon", "Summit Vodka"]);
        chart.Data[0].Values.Should().OnlyContain(v => double.IsFinite(v.Y));
    }

    [Fact]
    public void TryBuild_InventoryPayload_BuildsGaugeWithFiniteScore()
    {
        string inventory = JsonSerializer.Serialize(new
        {
            items = Array.Empty<object>(),
            total_items = 20,
            status_breakdown = new { healthy = 15, low = 3, critical = 1, out_of_stock = 1 },
            filters_applied = new { brand = "Pinnacle Hardware", region = "Midwest", category = (string?)null, status = (string?)null }
        });
        MeaiChatResponse response = ResponseWithToolResults(inventory);

        bool built = DeterministicChartBuilder.TryBuild(response, "gauge", out ChartSpec? chart);

        built.Should().BeTrue();
        chart!.Type.Should().Be("gauge");
        chart.Title.Should().Contain("Pinnacle Hardware");
        chart.Title.Should().Contain("Midwest");
        ChartDataPoint point = chart.Data[0].Values.Single();
        // 15 healthy / 20 total = 75%.
        point.Y.Should().Be(75.0);
        double.IsFinite(point.Y).Should().BeTrue();
        point.Y.Should().BeInRange(0, 100);
    }

    [Fact]
    public void TryBuild_NoChartableData_ReturnsFalse()
    {
        MeaiChatResponse response = ResponseWithToolResults(
            JsonSerializer.Serialize(new { message = "no data available", rows = Array.Empty<object>() }));

        DeterministicChartBuilder.TryBuild(response, "bar", out ChartSpec? chart).Should().BeFalse();
        chart.Should().BeNull();
    }

    // ── EnforceChartFulfillment invariant ───────────────────────────────────

    [Fact]
    public void EnforceChartFulfillment_ExplicitRequestNoChart_ReconstructsFromToolResults()
    {
        AgentExecutionPipeline pipeline = CreatePipeline();
        MeaiChatResponse response = ResponseWithToolResults(
            CompactedDemand("Sierra Gold Tequila", "Northeast", 820.0),
            CompactedDemand("Ridgeline Bourbon", "Northeast", 640.0),
            CompactedDemand("Summit Vodka", "Northeast", 910.0));

        var charts = new List<ChartSpec>();
        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            "Show me a bar chart comparing depletion velocity for all spirits brands in the Northeast",
            response, charts, "Here is the comparison.");

        result.Charts.Should().HaveCount(1);
        result.Charts[0].Type.Should().Be("bar");
        result.Charts[0].Data[0].Values.Should().HaveCount(3);
        result.Reply.Should().NotContain("Chart unavailable");
    }

    [Fact]
    public void EnforceChartFulfillment_ExplicitRequestNoData_AppendsStructuredDiagnostic()
    {
        AgentExecutionPipeline pipeline = CreatePipeline();
        MeaiChatResponse response = ResponseWithToolResults(
            JsonSerializer.Serialize(new { note = "nothing chartable" }));

        var charts = new List<ChartSpec>();
        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            "Show a gauge chart for Pinnacle Hardware inventory health in the Midwest",
            response, charts, "I could not find the data.");

        result.Charts.Should().BeEmpty();
        result.Reply.Should().Contain("Chart unavailable");
        result.Reply.Should().Contain("gauge chart");
    }

    [Fact]
    public void EnforceChartFulfillment_NonChartPrompt_DoesNotForceChart()
    {
        AgentExecutionPipeline pipeline = CreatePipeline();
        MeaiChatResponse response = ResponseWithToolResults(
            CompactedDemand("Summit Vodka", "Northeast", 910.0));

        var charts = new List<ChartSpec>();
        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            "How is the portfolio performing?", response, charts, "All green.");

        result.Charts.Should().BeEmpty("charts are never forced for non-chart prompts");
        result.Reply.Should().Be("All green.");
    }

    [Fact]
    public void EnforceChartFulfillment_AlreadyHasChart_LeavesItUnchanged()
    {
        AgentExecutionPipeline pipeline = CreatePipeline();
        MeaiChatResponse response = ResponseWithToolResults(
            CompactedDemand("Summit Vodka", "Northeast", 910.0));

        var existing = new ChartSpec
        {
            Type = "bar",
            Title = "Existing",
            Data = [new ChartSeries { Legend = "S", Values = [new ChartDataPoint { X = "A", Y = 1 }] }]
        };
        var charts = new List<ChartSpec> { existing };

        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            "Show me a bar chart of depletion velocity in the Northeast", response, charts, "Done.");

        result.Charts.Should().ContainSingle().Which.Should().BeSameAs(existing);
        result.Reply.Should().Be("Done.");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static AgentExecutionPipeline CreatePipeline()
    {
        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        return new AgentExecutionPipeline(
            AgentTestFixtures.CreateMockChatClient("{}"),
            AgentTestFixtures.CreateMockHubContext(),
            config,
            NullLogger<AgentExecutionPipeline>.Instance);
    }

    private static string CompactedDemand(string brand, string region, double avgWeekly) =>
        JsonSerializer.Serialize(new
        {
            period = new { start = "2024-01-01", end = "2024-12-31", months = 12 },
            filters = new { brand, region, channel = (string?)null },
            summary = new { total_volume = avgWeekly * 52, total_units = 1000, weeks_of_data = 52, avg_weekly_volume = avgWeekly },
            by_region = new object[]
            {
                new { region, volume = avgWeekly * 52, units = 1000, avg_weekly_volume = avgWeekly, weeks = 52 }
            },
            compaction = new { compacted = true, aggregate_complete = true }
        });

    private static MeaiChatResponse ResponseWithToolResults(params string[] toolResultJson)
    {
        var contents = new List<AIContent>();
        for (int i = 0; i < toolResultJson.Length; i++)
        {
            contents.Add(new FunctionResultContent($"call-{i}", toolResultJson[i]));
        }
        var message = new ChatMessage(ChatRole.Assistant, contents);
        return new MeaiChatResponse(message);
    }
}

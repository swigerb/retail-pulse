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
    public void EnforceChartFulfillment_ModelChartMeetingTheMarkFloor_IsKept_WhenNothingCanBeRebuilt()
    {
        // With no tool payloads there is nothing to rebuild from, so a model-emitted
        // chart is the fallback — and it is kept, provided it clears the mark floor
        // for its type.
        AgentExecutionPipeline pipeline = CreatePipeline();
        MeaiChatResponse response = ResponseWithToolResults();

        var existing = new ChartSpec
        {
            Type = "bar",
            Title = "Existing",
            Data =
            [
                new ChartSeries
                {
                    Legend = "S",
                    Values =
                    [
                        new ChartDataPoint { X = "A", Y = 1 },
                        new ChartDataPoint { X = "B", Y = 2 },
                        new ChartDataPoint { X = "C", Y = 3 },
                    ],
                },
            ],
        };
        var charts = new List<ChartSpec> { existing };

        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            "Show me a bar chart of depletion velocity in the Northeast", response, charts, "Done.");

        result.Charts.Should().ContainSingle();
        result.Charts[0].Type.Should().Be("bar");
        result.Charts[0].Data.Sum(s => s.Values.Count).Should().Be(3);
        result.Reply.Should().Be("Done.");
    }

    [Fact]
    public void EnforceChartFulfillment_PrefersTheDeterministicChart_OverAnUnderPopulatedModelChart()
    {
        // Issue #172: the tool payload is the source of truth. A model chart carrying
        // a single mark must not win over a chart the code can build from the data.
        AgentExecutionPipeline pipeline = CreatePipeline();
        MeaiChatResponse response = ResponseWithToolResults(
            CompactedDemand("Sierra Gold Tequila", "Northeast", 1893.2),
            CompactedDemand("Ridgeline Bourbon", "Northeast", 2109.5),
            CompactedDemand("Summit Vodka", "Northeast", 2296.3));

        var thin = new ChartSpec
        {
            Type = "bar",
            Title = "Model chart with one brand",
            Data = [new ChartSeries { Legend = "S", Values = [new ChartDataPoint { X = "Summit Vodka", Y = 2296.3 }] }]
        };
        var charts = new List<ChartSpec> { thin };

        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            "Show me a bar chart comparing depletion velocity for all spirits brands in the Northeast",
            response, charts, "Here you go.");

        result.Charts.Should().ContainSingle("the deterministic chart replaces the model's");
        result.Charts[0].Should().NotBeSameAs(thin);
        result.Charts[0].Data.Sum(s => s.Values.Count)
            .Should().BeGreaterThanOrEqualTo(3, "all three seeded Northeast spirits brands must appear");
    }

    [Fact]
    public void EnforceChartFulfillment_UnderPopulatedModelChart_IsDroppedRatherThanRendered()
    {
        // An under-populated chart is worse than no chart: it renders, so it looks
        // like success while misrepresenting the data. With nothing to rebuild from,
        // fail closed to the diagnostic.
        AgentExecutionPipeline pipeline = CreatePipeline();
        MeaiChatResponse response = ResponseWithToolResults();

        var thin = new ChartSpec
        {
            Type = "bar",
            Title = "One mark",
            Data = [new ChartSeries { Legend = "S", Values = [new ChartDataPoint { X = "A", Y = 1 }] }]
        };

        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            "Show me a bar chart of depletion velocity in the Northeast",
            response, [thin], "Done.");

        result.Charts.Should().BeEmpty();
        result.Reply.Should().Contain("Chart unavailable");
    }

    [Fact]
    public void EnforceChartFulfillment_UsesTheUsersOriginalRequest_NotAScopedStepRewrite()
    {
        // Issue #172: on the plan path the specialist receives a rewritten message —
        // "<step action> — original user request: <what the user typed>". If chart
        // intent were read from that rewrite, a step action mentioning a bar would
        // override the user's actual table request, and a bar would be emitted under
        // a "create a table …" ask. UserIntentMessage is what must drive detection.
        var scoped = new ChatRequest(
            Message: "Chart the regional rollup as a bar chart — original user request: "
                + "Create a table showing depletion stats for all home improvement brands by region",
            OriginalMessage: "Create a table showing depletion stats for all home improvement brands by region");

        scoped.UserIntentMessage.Should().Be(
            "Create a table showing depletion stats for all home improvement brands by region");

        ChartIntent fromRewrite = ChartRequestDetector.Detect(scoped.Message);
        ChartIntent fromUser = ChartRequestDetector.Detect(scoped.UserIntentMessage);

        fromRewrite.ChartType.Should().Be("bar", "the step action's own wording wins on the rewritten message");
        fromUser.ChartType.Should().Be("table", "the user's actual request is a table");
    }

    [Fact]
    public void ChatRequest_UserIntentMessage_FallsBackToMessage_OnTheSingleShotPath()
    {
        // No rewrite in play — Message already is what the user asked for.
        var direct = new ChatRequest("Create a pie chart showing market share breakdown");
        direct.UserIntentMessage.Should().Be("Create a pie chart showing market share breakdown");
    }

    [Fact]
    public void EnforceChartFulfillment_KeepsTheRicherModelChart_WhenTheRebuildCoversLessData()
    {
        // Deterministic-first must not cost completeness. A turn may have queried only
        // some regions, so the rebuild can be thinner than a valid model chart. The
        // tool payload is authoritative about what is TRUE, not about what is COMPLETE.
        AgentExecutionPipeline pipeline = CreatePipeline();
        MeaiChatResponse response = ResponseWithToolResults(
            CompactedDemand("FreshMart", "Northeast", 900.0),
            CompactedDemand("Harvest Table", "Northeast", 800.0));

        var richer = new ChartSpec
        {
            Type = "bar",
            Title = "Model chart covering more regions",
            Data =
            [
                new ChartSeries
                {
                    Legend = "FreshMart",
                    Values =
                    [
                        new ChartDataPoint { X = "Northeast", Y = 900 },
                        new ChartDataPoint { X = "Midwest", Y = 850 },
                        new ChartDataPoint { X = "Southeast", Y = 810 },
                        new ChartDataPoint { X = "West Coast", Y = 790 },
                    ],
                },
            ],
        };

        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            "Show me a bar chart of depletion velocity in the Northeast", response, [richer], "Done.");

        result.Charts.Should().ContainSingle();
        result.Charts[0].Data.Sum(s => s.Values.Count)
            .Should().Be(4, "the richer valid model chart is kept over a thinner rebuild");
    }

    [Fact]
    public void EnforceChartFulfillment_MergesPerRegionPayloads_IntoOneSeriesPerBrand()
    {
        // Issue #59: a turn may fan out region by region rather than fetching one
        // whole-country rollup per brand. Keeping only the first payload per brand threw
        // the rest away, so a two-brand comparison could collapse below the two-series
        // minimum and produce no chart at all despite ample data.
        AgentExecutionPipeline pipeline = CreatePipeline();
        MeaiChatResponse response = ResponseWithToolResults(
            CompactedDemand("FreshMart", "Northeast", 900.0),
            CompactedDemand("FreshMart", "Midwest", 850.0),
            CompactedDemand("FreshMart", "West Coast", 810.0),
            CompactedDemand("Harvest Table", "Northeast", 700.0),
            CompactedDemand("Harvest Table", "Midwest", 660.0),
            CompactedDemand("Harvest Table", "West Coast", 640.0));

        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            "Show a grouped bar chart comparing FreshMart and Harvest Table across all regions",
            response, [], "Here you go.");

        result.Charts.Should().ContainSingle();
        ChartSpec chart = result.Charts[0];
        chart.Type.Should().Be("groupedBar");
        chart.Data.Should().HaveCount(2, "one series per brand");
        chart.Data.Sum(s => s.Values.Count).Should().Be(6, "three regions for each of two brands");
    }

    [Fact]
    public void EnforceChartFulfillment_DoesNotDoubleCountARegionRepeatedForTheSameBrand()
    {
        // The same region reported twice for a brand is one fact, not two bars.
        AgentExecutionPipeline pipeline = CreatePipeline();
        MeaiChatResponse response = ResponseWithToolResults(
            CompactedDemand("FreshMart", "Northeast", 900.0),
            CompactedDemand("FreshMart", "Northeast", 900.0),
            CompactedDemand("FreshMart", "Midwest", 850.0),
            CompactedDemand("Harvest Table", "Northeast", 700.0),
            CompactedDemand("Harvest Table", "Midwest", 660.0));

        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            "Show a grouped bar chart comparing FreshMart and Harvest Table across all regions",
            response, [], "Here you go.");

        result.Charts.Should().ContainSingle();
        result.Charts[0].Data.Sum(s => s.Values.Count).Should().Be(4);
    }

    [Fact]
    public void EnforceChartFulfillment_BuildsABar_FromDepletionStatsWhenThatIsWhatTheTurnFetched()
    {
        // Issue #59: a brand comparison is answerable from either demand history or
        // depletion stats, and which one the model reaches for varies run to run. When it
        // picked depletion stats, the historical-demand builders found no
        // summary.total_volume / by_region fingerprint and the chart failed closed.
        AgentExecutionPipeline pipeline = CreatePipeline();
        MeaiChatResponse response = ResponseWithToolResults(
            DepletionStats("Sierra Gold Tequila", "Northeast", "4.2%"),
            DepletionStats("Ridgeline Bourbon", "Northeast", "-1.8%"),
            DepletionStats("Summit Vodka", "Northeast", "2.6%"));

        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            "Show me a bar chart comparing depletion velocity for all spirits brands in the Northeast",
            response, [], "Here you go.");

        result.Charts.Should().ContainSingle();
        result.Charts[0].Type.Should().Be("bar");
        result.Charts[0].Data.Sum(s => s.Values.Count).Should().Be(3, "one bar per brand");
    }

    [Fact]
    public void EnforceChartFulfillment_BuildsAGroupedBar_FromPerRegionDepletionStats()
    {
        AgentExecutionPipeline pipeline = CreatePipeline();
        MeaiChatResponse response = ResponseWithToolResults(
            DepletionStats("Coastline Tacos", "Northeast", "3.1%"),
            DepletionStats("Coastline Tacos", "Midwest", "1.4%"),
            DepletionStats("Apex Grill", "Northeast", "-2.2%"),
            DepletionStats("Apex Grill", "Midwest", "0.9%"));

        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            "Compare Coastline Tacos vs Apex Grill depletions across all regions",
            response, [], "Here you go.");

        result.Charts.Should().ContainSingle();
        result.Charts[0].Type.Should().Be("groupedBar");
        result.Charts[0].Data.Should().HaveCount(2, "one series per brand");
        result.Charts[0].Data.Sum(s => s.Values.Count).Should().Be(4);
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

    private static string DepletionStats(string brand, string region, string depletionsYoy) =>
        JsonSerializer.Serialize(new
        {
            brand,
            region,
            period = "YTD",
            metrics = new
            {
                depletions_yoy = depletionsYoy,
                sell_through_yoy = "-1.2%",
                inventory_weeks_on_hand = 8.4,
                status = "Healthy",
            },
            sentiment_summary = "Sample summary.",
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

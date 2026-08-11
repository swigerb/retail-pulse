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

    // ── Issue #76 Group A: chart emitted on prose prompts ───────────────────
    //
    // Production sweep found the LLM emitting ChartSpecs on prose prompts that
    // contain trigger nouns ("Compare X vs Y", "Show me … trends") but NO explicit
    // chart noun. The ChartRequestDetector unit test classified these as prose
    // correctly (see Detect_NonChartPrompt_IsNotExplicit above), but the
    // fulfillment path only enforced "chart must exist when requested" — never
    // the inverse. The specialist has CreateChart wired into its toolkit, so
    // nothing stopped the model from calling it on a prose prompt.

    [Theory]
    // The exact prompts from the #76 sweep failure taxonomy Group A (#1, #5, #14, #16, #17).
    [InlineData("Compare depletion trends across all regions for this quarter")]
    [InlineData("Compare Harvest Table vs FreshMart sell-through rates by region")]
    [InlineData("Give me a narrative on ClearDesk Technology vs Paper Products sell-through by region")]
    [InlineData("Show me Urban Living depletion trends across all regions this quarter")]
    [InlineData("Compare Foundry Home vs Urban Living performance in the West Coast")]
    public void EnforceChartFulfillment_ProsePromptWithModelEmittedChart_DropsChart(string prosePrompt)
    {
        // Precondition: the detector classifies this as prose. If this ever flips,
        // the invariant would be enforced from the other side — but the drop path
        // is the belt-and-braces guarantee against model non-determinism.
        ChartRequestDetector.Detect(prosePrompt).IsExplicitChartRequest.Should().BeFalse(
            "these prompts contain trigger nouns but no explicit chart noun");

        AgentExecutionPipeline pipeline = CreatePipeline();
        MeaiChatResponse response = ResponseWithToolResults(
            CompactedDemand("Sierra Gold Tequila", "Northeast", 820.0));

        // Simulate the production defect: the LLM produced a chart via CreateChart
        // on a prose prompt.
        var emitted = new ChartSpec
        {
            Type = "bar",
            Title = "Unrequested chart",
            Data = [new ChartSeries { Legend = "s", Values = [new ChartDataPoint { X = "A", Y = 1 }] }]
        };
        var charts = new List<ChartSpec> { emitted };

        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            prosePrompt, response, charts, "Here is the comparison.");

        result.Charts.Should().BeEmpty(
            "a prose prompt must never surface a chart, regardless of what the model emitted (issue #76 Group A)");
        result.Reply.Should().Be("Here is the comparison.",
            "the prose reply is untouched — only the unrequested chart is dropped");
    }

    // ── Issue #76 Group A: determinism ──────────────────────────────────────
    //
    // Chart-emission for a given prompt+chart-set must be deterministic — same input,
    // same decision. This guards against the #76 stability regression where the
    // same prompt sometimes charted and sometimes didn't across reruns.

    [Fact]
    public void EnforceChartFulfillment_ProsePrompt_IsDeterministicAcrossRepeatedCalls()
    {
        AgentExecutionPipeline pipeline = CreatePipeline();

        var results = new List<int>();
        for (int i = 0; i < 20; i++)
        {
            MeaiChatResponse response = ResponseWithToolResults(
                CompactedDemand("Summit Vodka", "Northeast", 910.0));
            var charts = new List<ChartSpec>
            {
                new()
                {
                    Type = "bar",
                    Title = $"Attempt {i}",
                    Data = [new ChartSeries { Legend = "s", Values = [new ChartDataPoint { X = "A", Y = i }] }]
                }
            };

            AgentExecutionPipeline.ChartFulfillmentResult r = pipeline.EnforceChartFulfillment(
                "Compare Harvest Table vs FreshMart sell-through rates by region",
                response, charts, "Comparison prose.");
            results.Add(r.Charts.Count);
        }

        results.Should().OnlyContain(c => c == 0,
            "identical prose prompts must yield identical (zero-chart) decisions across every rerun (issue #76 stability)");
    }

    [Fact]
    public void EnforceChartFulfillment_ExplicitChartPrompt_IsDeterministicAcrossRepeatedCalls()
    {
        AgentExecutionPipeline pipeline = CreatePipeline();

        var typeResults = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 20; i++)
        {
            MeaiChatResponse response = ResponseWithToolResults(
                CompactedDemand("Sierra Gold Tequila", "Northeast", 820.0),
                CompactedDemand("Ridgeline Bourbon", "Northeast", 640.0),
                CompactedDemand("Summit Vodka", "Northeast", 910.0));

            var charts = new List<ChartSpec>();
            AgentExecutionPipeline.ChartFulfillmentResult r = pipeline.EnforceChartFulfillment(
                "Show me a bar chart comparing depletion velocity for all spirits brands in the Northeast",
                response, charts, "Here you go.");
            typeResults.Add(r.Charts.Single().Type);
        }

        typeResults.Should().ContainSingle().Which.Should().Be("bar",
            "identical explicit-chart prompts must yield the same chart type on every rerun");
    }

    // ── Issue #76 Group D: user-stated chart type must win ──────────────────

    [Fact]
    public void EnforceChartFulfillment_UserAskedBar_ModelEmittedHorizontalBar_CoercesToBar()
    {
        // The exact #20 failure from the sweep: prompt asks for "bar chart" but the
        // model emitted a horizontalBar. User's stated type must win.
        AgentExecutionPipeline pipeline = CreatePipeline();
        MeaiChatResponse response = ResponseWithToolResults(
            CompactedDemand("Sierra Gold Tequila", "Northeast", 820.0));

        var modelChart = new ChartSpec
        {
            Type = "horizontalBar",
            Title = "Depletion velocity - Northeast",
            Data =
            [
                new ChartSeries
                {
                    Legend = "Avg Weekly Depletion Velocity",
                    Values =
                    [
                        new ChartDataPoint { X = "Sierra Gold Tequila", Y = 820.0 },
                        new ChartDataPoint { X = "Ridgeline Bourbon",   Y = 640.0 },
                        new ChartDataPoint { X = "Summit Vodka",        Y = 910.0 }
                    ]
                }
            ]
        };
        var charts = new List<ChartSpec> { modelChart };

        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            "Show me a bar chart comparing depletion velocity for all spirits brands in the Northeast",
            response, charts, "Here is the bar chart.");

        result.Charts.Should().ContainSingle();
        result.Charts[0].Type.Should().Be("bar",
            "the user explicitly asked for a bar chart — the model's horizontalBar drift must be corrected (issue #76 Group D)");
        result.Charts[0].Data.Should().BeEquivalentTo(modelChart.Data,
            "coercion is a rendering-orientation fix; the underlying data must be preserved verbatim");
    }

    [Fact]
    public void EnforceChartFulfillment_CrossFamilyMismatch_LeavesTypeAlone()
    {
        // If the model emits e.g. a pie chart when the user asked for a bar chart,
        // the data shapes are not interchangeable — the coercion path is a no-op
        // and the existing invariants (roster coverage, structured diagnostic)
        // handle it. This guards the safety of the coercion.
        AgentExecutionPipeline pipeline = CreatePipeline();
        MeaiChatResponse response = ResponseWithToolResults(
            CompactedDemand("Sierra Gold Tequila", "Northeast", 820.0));

        var modelChart = new ChartSpec
        {
            Type = "pie",
            Title = "Unrelated pie",
            Data = [new ChartSeries { Legend = "s", Values = [new ChartDataPoint { X = "A", Y = 1 }] }]
        };
        var charts = new List<ChartSpec> { modelChart };

        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            "Show me a bar chart of depletion velocity in the Northeast",
            response, charts, "Here.");

        result.Charts[0].Type.Should().Be("pie",
            "cross-family type mismatches are not silently coerced — data shapes would not bind");
    }

    [Fact]
    public void EnforceChartFulfillment_HorizontalBarRankingRosterComplete_NotCoercedByBarKeyword()
    {
        // Belt-and-braces: the #74 horizontal-bar ranking invariant must not be
        // undone by this new coercion. When the user explicitly asked for
        // "horizontal bar chart", the detector reports chartType=horizontalBar
        // (no coercion applies), and the roster-coverage branch owns the chart.
        AgentExecutionPipeline pipeline = CreatePipelineWithRoster(["A", "B", "C", "D", "E", "F"]);
        MeaiChatResponse response = ResponseWithToolResults(PortfolioDepletionPayload(
            ("A", 10.0), ("B", 20.0), ("C", 30.0), ("D", 40.0), ("E", 50.0), ("F", 60.0)));

        var charts = new List<ChartSpec>();
        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            "Show a horizontal bar chart ranking all brands by depletion growth rate",
            response, charts, "Ranking.");

        result.Charts.Should().ContainSingle();
        result.Charts[0].Type.Should().Be("horizontalBar",
            "explicit horizontalBar request stays horizontalBar — the #74 gate must be preserved");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static AgentExecutionPipeline CreatePipelineWithRoster(IEnumerable<string> brands)
    {
        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var tenant = new TenantConfiguration
        {
            Company = "TestCo",
            BrandsList = [.. brands.Select(b => new BrandConfig { Name = b, Category = "spirits" })],
        };
        return new AgentExecutionPipeline(
            AgentTestFixtures.CreateMockChatClient("{}"),
            hubContext: AgentTestFixtures.CreateMockHubContext(),
            streamingHubContext: null,
            streamingFeature: null,
            configuration: config,
            logger: NullLogger<AgentExecutionPipeline>.Instance,
            metrics: null,
            anonymousChatPolicy: Api.Auth.NoOpAnonymousChatPolicy.Instance,
            tenant: tenant);
    }

    private static string PortfolioDepletionPayload(params (string Brand, double GrowthYoy)[] rows) =>
        JsonSerializer.Serialize(new
        {
            region = "All Regions",
            period = "YTD",
            brandCount = rows.Length,
            brands = rows.Select(r => new
            {
                brand = r.Brand,
                region = "All Regions",
                metrics = new
                {
                    depletions_yoy = (r.GrowthYoy >= 0 ? "+" : "") + r.GrowthYoy.ToString("0.0") + "%",
                    sell_through_yoy = "+1.2%",
                    inventory_weeks_on_hand = 6.5,
                    status = "OnTrack",
                },
            }).ToArray(),
        });

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

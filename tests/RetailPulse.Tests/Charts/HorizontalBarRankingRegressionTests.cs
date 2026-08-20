using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Budget;
using RetailPulse.Api.Charts;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Charts;
using Xunit;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;

namespace RetailPulse.Tests.Charts;

/// <summary>
/// Regression coverage for the horizontal-bar "rank all brands by depletion growth
/// rate" prompt (issue #50 case + follow-through). The failure mode this test
/// guards against: the LLM legitimately calls <c>GetPortfolioDepletionStats</c>
/// and receives a complete seeded payload, but the deterministic builder can't
/// materialise a <see cref="ChartSpec"/> — so the pipeline falls back to
/// narration + inline JSON, the frontend then sees raw JSON in the assistant
/// bubble, and the "data absence" branch fires despite complete data.
/// The manifest matrix test already covers the happy path; this one pins:
///
/// 1. A COMPLETE seeded portfolio payload (every brand carries a finite
///    <c>depletions_yoy</c> percent) yields a <c>horizontalBar</c> ChartSpec with
///    one series and exactly the seeded brand count as marks, ordered strictly
///    descending — never zero-filling missing brands.
/// 2. A PARTIALLY complete payload (three brands with missing/unparseable growth)
///    yields a chart with only the well-formed brands — the missing ones are
///    excluded, not charted as zero. The <c>ChartSpecValidator</c> still
///    considers the chart renderable (≥2 marks).
/// 3. An EMPTY payload (no brands with finite growth) causes the deterministic
///    builder to return false, so the caller can produce an honest "no data"
///    response rather than a chart of zeroes.
/// </summary>
public sealed class HorizontalBarRankingRegressionTests
{
    private const string RegionAll = "All Regions";

    [Fact]
    public void CompleteSeededPortfolio_YieldsHorizontalBar_WithAllBrandsRankedDescending()
    {
        string[] brands =
        [
            "Sierra Gold Tequila", "Ridgeline Bourbon", "Summit Vodka",
            "FreshMart", "Harvest Table", "Apex Grill", "Coastline Tacos",
            "Pinnacle Hardware", "Summit Outdoor",
        ];
        double[] growthPct = [-2.5, -1.6, -0.7, 0.2, 1.1, 2.0, 2.9, 3.8, 4.7];

        string payload = BuildPortfolioDepletionPayload(brands, growthPct);
        string compacted = new PortfolioDepletionCompactor()
            .Compact("GetPortfolioDepletionStats", payload, new ToolResultBudgetOptions())
            .Json;

        MeaiChatResponse response = ResponseWithToolResults(compacted);

        DeterministicChartBuilder
            .TryBuild(response, requestedType: "horizontalBar", out ChartSpec? chart)
            .Should().BeTrue("a fully seeded portfolio payload must build a horizontal-bar ranking");

        chart.Should().NotBeNull();
        chart.Type.Should().Be("horizontalBar");
        chart.Data.Should().HaveCount(1, "the ranking is single-series");

        var points = chart.Data[0].Values.ToList();
        points.Should().HaveCount(brands.Length,
            "every seeded brand with a finite growth value must appear — never dropped or zero-filled");

        for (int i = 1; i < points.Count; i++)
        {
            points[i].Y.Should().BeLessThanOrEqualTo(points[i - 1].Y,
                "brands must be ordered strictly descending by growth rate");
        }

        // The renderable-validator gate (what the frontend uses) must agree the
        // chart is bindable — this is what prevents the false "data absence" path.
        ChartSpecValidator
            .TryGetRenderable(chart, minSeries: 1, minMarks: brands.Length, out ChartSpec? renderable)
            .Should().BeTrue();
        renderable.Should().NotBeNull();
    }

    [Fact]
    public void PartiallyCompletePortfolio_ExcludesBrandsWithMissingGrowth_NeverZeroFills()
    {
        // Three brands carry finite growth; six carry a null/blank/NaN metric.
        // The chart must contain three marks, not nine, and none of them should
        // be zero unless the seeded data says so.
        string[] brands =
        [
            "Sierra Gold Tequila", "Ridgeline Bourbon", "Summit Vodka",
            "FreshMart", "Harvest Table", "Apex Grill", "Coastline Tacos",
            "Pinnacle Hardware", "Summit Outdoor",
        ];
        string?[] growthValues =
        [
            "-2.5%", "1.1%", "3.8%",
            null, "", "n/a", "not-a-number", null, null,
        ];

        string payload = BuildPortfolioDepletionPayloadWithRawGrowth(brands, growthValues);
        string compacted = new PortfolioDepletionCompactor()
            .Compact("GetPortfolioDepletionStats", payload, new ToolResultBudgetOptions())
            .Json;

        MeaiChatResponse response = ResponseWithToolResults(compacted);

        DeterministicChartBuilder
            .TryBuild(response, requestedType: "horizontalBar", out ChartSpec? chart)
            .Should().BeTrue();

        chart.Should().NotBeNull("TryBuild returned true and the chart must be materialised");
        chart.Data[0].Values.Should().HaveCount(3,
            "brands with missing/unparseable growth must be excluded, never charted as zero");
        chart.Data[0].Values.Select(p => p.X)
            .Should().BeEquivalentTo(["Summit Vodka", "Ridgeline Bourbon", "Sierra Gold Tequila"]);
    }

    [Fact]
    public void EmptyPortfolio_ReturnsFalse_SoCallerCanReportHonestDataAbsence()
    {
        string payload = BuildPortfolioDepletionPayloadWithRawGrowth(
            brands: ["Sierra Gold Tequila"],
            rawGrowthPercents: [null]);
        string compacted = new PortfolioDepletionCompactor()
            .Compact("GetPortfolioDepletionStats", payload, new ToolResultBudgetOptions())
            .Json;

        MeaiChatResponse response = ResponseWithToolResults(compacted);

        DeterministicChartBuilder
            .TryBuild(response, requestedType: "horizontalBar", out ChartSpec? chart)
            .Should().BeFalse(
                "with zero brands carrying finite growth, the builder must refuse — the caller then reports 'no data' honestly");
        chart.Should().BeNull();
    }

    // ── payload helpers ─────────────────────────────────────────────────────

    private static string BuildPortfolioDepletionPayload(string[] brands, double[] growthPct)
    {
        var rows = brands.Select((b, i) => new
        {
            brand = b,
            region = RegionAll,
            metrics = new
            {
                depletions_yoy = FormatSigned(growthPct[i]),
                sell_through_yoy = "+1.2%",
                inventory_weeks_on_hand = 6.5 + (i * 0.1),
                status = "OnTrack",
            },
            sentiment_summary = "verbose prose the compactor strips",
        }).ToArray();
        return JsonSerializer.Serialize(new
        {
            region = RegionAll,
            period = "YTD",
            brandCount = rows.Length,
            brands = rows,
        });
    }

    private static string BuildPortfolioDepletionPayloadWithRawGrowth(
        string[] brands,
        string?[] rawGrowthPercents)
    {
        var rows = brands.Select((b, i) => new
        {
            brand = b,
            region = RegionAll,
            metrics = new
            {
                depletions_yoy = rawGrowthPercents[i],
                sell_through_yoy = "+1.2%",
                inventory_weeks_on_hand = 6.5 + (i * 0.1),
                status = "OnTrack",
            },
        }).ToArray();
        return JsonSerializer.Serialize(new
        {
            region = RegionAll,
            period = "YTD",
            brandCount = rows.Length,
            brands = rows,
        });
    }

    private static string FormatSigned(double value) =>
        value >= 0 ? $"+{value:0.0}%" : $"{value:0.0}%";

    private static MeaiChatResponse ResponseWithToolResults(params string[] compactedPayloads)
    {
        var messages = new List<ChatMessage>();
        foreach (string payload in compactedPayloads)
        {
            var toolMessage = new ChatMessage(ChatRole.Tool, [new FunctionResultContent(
                callId: Guid.NewGuid().ToString("N"),
                result: payload)]);
            messages.Add(toolMessage);
        }

        messages.Add(new ChatMessage(
            ChatRole.Assistant,
            "Here is the depletion-growth ranking across the portfolio."));

        return new MeaiChatResponse(messages);
    }
}

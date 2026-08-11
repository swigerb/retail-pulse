using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Budget;
using RetailPulse.Api.Charts;
using RetailPulse.Contracts;
using Xunit;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;

namespace RetailPulse.Tests.Charts;

/// <summary>
/// Regression coverage for issue #74 — enforces the P0 acceptance contract for
/// the horizontal-bar "rank all brands by depletion growth rate" prompt beyond
/// the pre-existing happy-path assertions in
/// <see cref="HorizontalBarRankingRegressionTests"/>:
///
///  1. A COMPLETE portfolio payload covering ALL 12 tenant brands yields a chart
///     with ≥ 6 finite marks and at least one non-zero mark — never a zero shell.
///  2. A ranking chart with only velocity data (GetHistoricalDemand only, no
///     brands[] aggregate) FAILS CLOSED — the builder must not rebrand a velocity
///     bar as growth.
///  3. A brand whose YoY is legitimately 0% is included, but a chart of purely
///     zero marks is rejected as an empty shell.
/// </summary>
public sealed class HorizontalBarRankingContractTests
{
    // All 12 tenant.yaml brands.
    private static readonly string[] AllTenantBrands =
    [
        "Sierra Gold Tequila", "Ridgeline Bourbon", "Summit Vodka",
        "FreshMart", "Harvest Table",
        "Apex Grill", "Coastline Tacos",
        "Pinnacle Hardware", "Summit Outdoor",
        "ClearDesk",
        "Urban Living", "Foundry Home",
    ];

    [Fact]
    public void AllTwelveTenantBrands_Produce_SixOrMore_FiniteMarks()
    {
        double[] growthPct = [-3.5, -2.4, -1.6, -0.7, 0.4, 1.1, 2.0, 2.9, 3.8, 4.7, 5.6, 6.5];
        string payload = BuildPayload(AllTenantBrands, growthPct);
        string compacted = new PortfolioDepletionCompactor()
            .Compact("GetPortfolioDepletionStats", payload, new ToolResultBudgetOptions())
            .Json;

        MeaiChatResponse response = ToolResponse(compacted);

        DeterministicChartBuilder
            .TryBuild(response, requestedType: "horizontalBar", minMarks: 6, out ChartSpec? chart)
            .Should().BeTrue("a complete 12-brand payload must yield the growth ranking");

        chart.Should().NotBeNull();
        chart!.Type.Should().Be("horizontalBar");
        chart.Data.Should().HaveCount(1);
        chart.Data[0].Values.Should().HaveCount(AllTenantBrands.Length,
            "all 12 seeded brands must appear as marks");

        int nonZeroCount = chart.Data[0].Values.Count(p => p.Y != 0.0 && double.IsFinite(p.Y));
        nonZeroCount.Should().BeGreaterThanOrEqualTo(6,
            "the ranking must carry at least 6 non-zero, meaningful marks");
    }

    [Fact]
    public void Growth_Ranking_Includes_ZeroYoy_Brands_But_Requires_Overall_Signal()
    {
        // One brand at 0%, the rest non-zero. Overall chart must still surface with
        // ≥ 6 non-zero marks.
        double[] growth = [0.0, -2.4, -1.6, -0.7, 0.4, 1.1, 2.0, 2.9, 3.8, 4.7, 5.6, 6.5];
        string payload = BuildPayload(AllTenantBrands, growth);

        MeaiChatResponse response = ToolResponse(payload);

        DeterministicChartBuilder
            .TryBuild(response, requestedType: "horizontalBar", minMarks: 6, out ChartSpec? chart)
            .Should().BeTrue();

        chart!.Data[0].Values.Should().Contain(p => p.Y == 0.0,
            "a legitimately-zero brand IS charted (zero is a real value)");
        chart.Data[0].Values.Count(p => p.Y != 0.0 && double.IsFinite(p.Y))
            .Should().BeGreaterThanOrEqualTo(6);
    }

    [Fact]
    public void AllZeroGrowth_Payload_FailsClosed_NoZeroShell()
    {
        // Every brand at 0.0%. Even with 12 finite marks this must NOT produce a
        // ranking chart — a zero shell is the exact P0 DOM outcome we forbid.
        double[] zeros = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        string payload = BuildPayload(AllTenantBrands, zeros);
        MeaiChatResponse response = ToolResponse(payload);

        DeterministicChartBuilder
            .TryBuild(response, requestedType: "horizontalBar", minMarks: 6, out ChartSpec? chart)
            .Should().BeFalse("a chart of exclusively-zero marks paints as an empty shell — reject it");
        chart.Should().BeNull();
    }

    [Fact]
    public void ChartFulfillment_HorizontalBarRanking_FailsClosed_WhenOnlyVelocityDataPresent()
    {
        // Simulate the failure mode: only compacted GetHistoricalDemand payloads are
        // present in the response (no brands[] aggregate). A horizontal-bar RANKING
        // ask must NOT surface a velocity bar rebranded as growth — the deterministic
        // builder must return null so the caller emits chart-unavailable.
        string velocityPayload = BuildHistoricalDemandPayload("Ridgeline Bourbon", 1200.0);
        MeaiChatResponse response = ToolResponse(velocityPayload, BuildHistoricalDemandPayload("Summit Vodka", 950.0));

        DeterministicChartBuilder
            .TryBuild(response, requestedType: "horizontalBar", minMarks: 6, out ChartSpec? chart)
            .Should().BeFalse("horizontal-bar ranking must never rebrand velocity as growth");
        chart.Should().BeNull();
    }

    // ── payload helpers ─────────────────────────────────────────────────────

    private static string BuildPayload(string[] brands, double[] growthPct)
    {
        var rows = brands.Select((b, i) => new
        {
            brand = b,
            region = "National",
            metrics = new
            {
                depletions_yoy = FormatSigned(growthPct[i]),
                sell_through_yoy = "+1.0%",
                inventory_weeks_on_hand = 6.0,
                status = "OnTrack",
            }
        }).ToArray();
        return JsonSerializer.Serialize(new
        {
            region = "National",
            period = "YTD",
            brandCount = rows.Length,
            brands = rows,
        });
    }

    private static string BuildHistoricalDemandPayload(string brand, double avgWeekly)
    {
        return JsonSerializer.Serialize(new
        {
            brand,
            region = "National",
            summary = new
            {
                total_volume = avgWeekly * 52,
                avg_weekly_volume = avgWeekly,
                weeks_of_data = 52,
            },
            by_region = new[]
            {
                new { region = "Northeast", total_volume = avgWeekly * 12 },
                new { region = "Southeast", total_volume = avgWeekly * 12 },
            },
            compaction = new { compacted = true, aggregate_complete = true },
        });
    }

    private static string FormatSigned(double v) => v >= 0 ? $"+{v:0.0}%" : $"{v:0.0}%";

    private static MeaiChatResponse ToolResponse(params string[] payloads)
    {
        var messages = new List<ChatMessage>();
        foreach (string p in payloads)
        {
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(
                callId: Guid.NewGuid().ToString("N"),
                result: p)]));
        }
        messages.Add(new ChatMessage(ChatRole.Assistant, "Here is the ranking."));
        return new MeaiChatResponse(messages);
    }
}

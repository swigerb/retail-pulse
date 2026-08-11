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
/// <see cref="HorizontalBarRankingRegressionTests"/>.
///
/// The tenant roster is loaded from <c>tenant.yaml</c> so the assertions are
/// count-driven and stay correct if brands are added or removed. The contract:
///
///  1. A COMPLETE portfolio payload covering EVERY tenant brand yields a chart
///     with one mark per brand and at least one non-zero mark — never a zero
///     shell, never a subset.
///  2. A ranking chart with only velocity data (GetHistoricalDemand only, no
///     brands[] aggregate) FAILS CLOSED — the builder must not rebrand a velocity
///     bar as growth.
///  3. A brand whose YoY is legitimately 0% is included, but a chart of purely
///     zero marks is rejected as an empty shell.
///  4. When the caller supplies the tenant roster and the payload is missing
///     ANY brand from that roster, the builder FAILS CLOSED — the exact P0
///     failure mode (6 of 12 brands silently returned) is now caught here.
/// </summary>
public sealed class HorizontalBarRankingContractTests
{
    private static readonly TenantConfiguration Tenant = LoadTenant();
    private static readonly string[] AllTenantBrands = [.. Tenant.Brands.Select(b => b.Name)];

    private static TenantConfiguration LoadTenant()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tenantPath = Path.Combine(repoRoot, "tenant.yaml");
        return new FileTenantProvider(tenantPath).GetTenant();
    }

    [Fact]
    public void AllTenantBrands_Produce_OneMarkPerBrand_WithNonZeroSignal()
    {
        // Growth vector sized to the tenant roster; every entry non-zero so the
        // "at least one non-zero mark" contract is met regardless of roster size.
        double[] growthPct = [.. Enumerable.Range(0, AllTenantBrands.Length).Select(i => Math.Round(-5.0 + (i * 1.1), 1))];
        string payload = BuildPayload(AllTenantBrands, growthPct);
        string compacted = new PortfolioDepletionCompactor()
            .Compact("GetPortfolioDepletionStats", payload, new ToolResultBudgetOptions())
            .Json;

        MeaiChatResponse response = ToolResponse(compacted);

        DeterministicChartBuilder
            .TryBuild(response, requestedType: "horizontalBar", minMarks: AllTenantBrands.Length, out ChartSpec? chart)
            .Should().BeTrue("a complete tenant-roster payload must yield the growth ranking");

        chart.Should().NotBeNull();
        chart.Type.Should().Be("horizontalBar");
        chart.Data.Should().HaveCount(1);
        chart.Data[0].Values.Should().HaveCount(AllTenantBrands.Length,
            "every tenant brand must appear as a mark — no silent drops");

        int nonZeroCount = chart.Data[0].Values.Count(p => p.Y != 0.0 && double.IsFinite(p.Y));
        nonZeroCount.Should().BeGreaterThan(0, "the ranking must carry a real signal, not an empty shell");
    }

    [Fact]
    public void RosterCoverageContract_FailsClosed_WhenAnyTenantBrandIsMissing()
    {
        // Reproduces the exact production failure: only HALF the tenant brands come
        // through the aggregate payload. With the tenant roster supplied, the builder
        // MUST fail closed rather than emit a chart that silently mis-ranks the
        // portfolio. This is the assertion that would have caught the P0 regression.
        int half = Math.Max(2, AllTenantBrands.Length / 2);
        string[] presentBrands = [.. AllTenantBrands.Take(half)];
        double[] growth = [.. Enumerable.Range(0, half).Select(i => (double)(i + 1))];

        string payload = BuildPayload(presentBrands, growth);
        MeaiChatResponse response = ToolResponse(payload);

        bool ok = DeterministicChartBuilder.TryBuild(
            response,
            requestedType: "horizontalBar",
            minMarks: 2,
            requiredBrands: AllTenantBrands,
            out ChartSpec? chart);

        ok.Should().BeFalse(
            $"payload covers only {half}/{AllTenantBrands.Length} tenant brands — must fail closed");
        chart.Should().BeNull();
    }

    [Fact]
    public void RosterCoverageContract_Succeeds_WhenAllTenantBrandsPresent_IncludingZeros()
    {
        // Complete roster with one legitimately-zero brand mixed in. Coverage passes,
        // zero mark is present, chart is emitted with all brands.
        double[] growth = [.. Enumerable.Range(0, AllTenantBrands.Length).Select(i => i == 0 ? 0.0 : Math.Round(-4.0 + (i * 0.9), 1))];
        string payload = BuildPayload(AllTenantBrands, growth);
        MeaiChatResponse response = ToolResponse(payload);

        bool ok = DeterministicChartBuilder.TryBuild(
            response,
            requestedType: "horizontalBar",
            minMarks: AllTenantBrands.Length,
            requiredBrands: AllTenantBrands,
            out ChartSpec? chart);

        ok.Should().BeTrue();
        chart.Should().NotBeNull();
        chart.Data[0].Values.Should().HaveCount(AllTenantBrands.Length);
        chart.Data[0].Values.Any(p => p.Y == 0.0).Should().BeTrue(
            "a legitimately-zero brand IS charted (zero is a real value)");
        DeterministicChartBuilder.CoversRoster(chart, AllTenantBrands).Should().BeTrue();
    }

    [Fact]
    public void CoversRoster_ReturnsFalse_ForModelChartMissingBrands()
    {
        // A model-emitted chart that dropped half the portfolio — CoversRoster is what
        // the fulfillment path uses to decide whether to REPLACE it with the
        // deterministic reconstruction. This test pins that decision.
        int half = AllTenantBrands.Length / 2;
        var partial = new ChartSpec
        {
            Type = "horizontalBar",
            Title = "Brands Ranked by Depletion Growth Rate (YoY)",
            XAxisTitle = "%",
            YAxisTitle = "Brand",
            Data =
            [
                new ChartSeries
                {
                    Legend = "Depletion Growth Rate % (YoY)",
                    Values = [.. AllTenantBrands.Take(half).Select((b, i) => new ChartDataPoint { X = b, Y = i + 1.0 })],
                }
            ],
        };

        DeterministicChartBuilder.CoversRoster(partial, AllTenantBrands).Should().BeFalse(
            "the exact P0 shape (6 of 12 marks) must not be considered roster-covering");
    }

    [Fact]
    public void Growth_Ranking_Includes_ZeroYoy_Brands_But_Requires_Overall_Signal()
    {
        // One brand at 0%, the rest non-zero.
        double[] growth = [.. Enumerable.Range(0, AllTenantBrands.Length).Select(i => i == 0 ? 0.0 : Math.Round(-3.0 + (i * 0.8), 1))];
        string payload = BuildPayload(AllTenantBrands, growth);

        MeaiChatResponse response = ToolResponse(payload);

        DeterministicChartBuilder
            .TryBuild(response, requestedType: "horizontalBar", minMarks: AllTenantBrands.Length, out ChartSpec? chart)
            .Should().BeTrue();

        chart!.Data[0].Values.Should().Contain(p => p.Y == 0.0,
            "a legitimately-zero brand IS charted (zero is a real value)");
        chart.Data[0].Values.Count(p => p.Y != 0.0 && double.IsFinite(p.Y))
            .Should().BeGreaterThan(0);
    }

    [Fact]
    public void AllZeroGrowth_Payload_FailsClosed_NoZeroShell()
    {
        // Every brand at 0.0%. Even with a full roster of finite marks this must NOT
        // produce a ranking chart — a zero shell is the exact P0 DOM outcome we forbid.
        double[] zeros = new double[AllTenantBrands.Length];
        string payload = BuildPayload(AllTenantBrands, zeros);
        MeaiChatResponse response = ToolResponse(payload);

        DeterministicChartBuilder
            .TryBuild(response, requestedType: "horizontalBar", minMarks: AllTenantBrands.Length, out ChartSpec? chart)
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


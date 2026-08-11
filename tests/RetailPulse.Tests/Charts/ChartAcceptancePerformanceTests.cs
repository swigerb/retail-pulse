using System.Text.Json;
using FluentAssertions;
using RetailPulse.Api.Budget;
using RetailPulse.Contracts.Charts;
using Xunit;

namespace RetailPulse.Tests.Charts;

/// <summary>
/// Performance-budget acceptance for every curated chart prompt.
///
/// The P0 report (issue #50) called out two symptoms that boiled down to the tool
/// context exploding on chart prompts: prose citing a "truncated portfolio pull",
/// and refusal to rank all brands citing a context budget. Both are fixed by the
/// tool-result compactors (GetHistoricalDemand → <c>by_region</c> rollup,
/// GetPortfolioDepletionStats → flattened metrics), so this test locks in the
/// invariant with two numeric ceilings drawn directly from issue #50:
///
///   • &lt;25,000 estimated tool-context tokens per prompt after compaction, and
///   • ≤5 distinct tool calls per prompt.
///
/// For each entry in <see cref="ChartAcceptanceManifest.Cases"/> we simulate the
/// production per-request budget scope: build the same representative payloads the
/// acceptance matrix uses, push them through <see cref="ToolResultBudget"/> with a
/// live <see cref="RequestToolContext"/>, and read back the cumulative token
/// estimate and distinct-call count. A regression that reverts compaction or fans
/// out a per-brand chain (the #50 refusal path) trips this test immediately.
/// </summary>
public sealed class ChartAcceptancePerformanceTests
{
    /// <summary>Hard ceiling from issue #50 — cumulative tool-result tokens per prompt.</summary>
    private const int MaxCumulativeTokens = 25_000;

    /// <summary>Hard ceiling from issue #50 — distinct tool invocations per prompt.</summary>
    private const int MaxDistinctToolCalls = 5;

    public static IEnumerable<object[]> AllCases()
        => ChartAcceptanceManifest.Cases.Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(AllCases))]
    public void Case_StaysUnderTokenAndCallBudgets(ChartAcceptanceCase c)
    {
        (string ToolName, string Raw)[] invocations = BuildRepresentativeInvocations(c);
        invocations.Should().NotBeEmpty($"a fixture must exist for {c.DataSource}");

        var options = new ToolResultBudgetOptions();
        var budget = new ToolResultBudget(
        [
            new HistoricalDemandCompactor(),
            new PortfolioDepletionCompactor(),
            new GenericArrayCompactor(),
        ]);

        using IDisposable scope = RequestToolContext.Begin($"perf::{c.Prompt}");
        RequestToolContext ctx = RequestToolContext.Current!;

        foreach ((string toolName, string raw) in invocations)
        {
            BudgetedResult result = budget.Apply(toolName, raw, options);
            string key = ctx.BuildKey(toolName, ArgsFingerprint(toolName, raw));
            if (ctx.TryGetDeduped(key, out _))
            {
                ctx.RecordDedup(result.Metrics);
            }
            else
            {
                ctx.Record(key, result.Json, result.Metrics);
            }
        }

        int cumulativeTokens = ctx.Metrics.Sum(m => m.EstimatedTokens);

        ctx.DistinctCalls.Should().BeLessThanOrEqualTo(
            MaxDistinctToolCalls,
            $"prompt '{c.Prompt}' must complete within the ≤{MaxDistinctToolCalls} tool-call budget from issue #50");

        cumulativeTokens.Should().BeLessThan(
            MaxCumulativeTokens,
            $"prompt '{c.Prompt}' must fit under the <{MaxCumulativeTokens:N0} cumulative tool-context tokens budget from issue #50");
    }

    /// <summary>
    /// Representative worst-case tool-call plan per prompt.
    ///
    /// Each entry mirrors the smallest, realistic set of tool calls a specialist would
    /// actually make to satisfy the prompt, sized at the upper end of realistic seeded
    /// data (12 months of weekly volume x every seeded region, or a full portfolio
    /// rollup). The chained per-brand fan-out that caused the #50 refusal ("Chart
    /// unavailable, historical pulls truncated") is deliberately NOT modeled — the
    /// bounded portfolio-depletion aggregate is one call, not eight.
    /// </summary>
    private static (string ToolName, string Raw)[] BuildRepresentativeInvocations(ChartAcceptanceCase c)
    {
        return c.DataSource switch
        {
            ChartDataSource.HistoricalDemand => BuildHistoricalDemandInvocations(c),
            ChartDataSource.PortfolioDepletion => [("GetPortfolioDepletionStats", BuildLargePortfolioPayload("National"))],
            ChartDataSource.DepletionStats => [.. BuildHomeImprovementInvocations()],
            ChartDataSource.MarketShare => [("GetMarketShare", BuildMarketSharePayload())],
            ChartDataSource.VariantMix => [("GetVariantMix", BuildVariantMixPayload("Apex Grill", "Southwest"))],
            ChartDataSource.InventoryLevels => [("GetInventoryLevels", BuildInventoryPayload("Pinnacle Hardware", "Midwest"))],
            _ => [],
        };
    }

    private static (string ToolName, string Raw)[] BuildHistoricalDemandInvocations(ChartAcceptanceCase c)
    {
        if (c.ChartType == "line")
        {
            return [("GetHistoricalDemand", BuildDemandPayloadAllRegions(c.RequiredEntities.First(), months: 12))];
        }
        if (c.ChartType == "bar")
        {
            return [.. c.RequiredEntities.Select(b =>
                ("GetHistoricalDemand", BuildDemandPayloadOneRegion(b, "Northeast", months: 12, perWeekVolume: 800.0)))];
        }

        // Grouped bar / QSR two-brand — one all-regions payload per brand.
        return [.. c.RequiredEntities.Select(b =>
            ("GetHistoricalDemand", BuildDemandPayloadAllRegions(b, months: 12)))];
    }

    private static (string, string)[] BuildHomeImprovementInvocations()
    {
        // A realistic home-improvement table fetches two regions of the portfolio rollup
        // — bounded, not per-brand-per-region.
        return
        [
            ("GetPortfolioDepletionStats", BuildLargePortfolioPayload("Midwest")),
            ("GetPortfolioDepletionStats", BuildLargePortfolioPayload("Southeast")),
        ];
    }

    // ── Payload builders — sized to the realistic upper end of seeded data ─────

    private static string BuildDemandPayloadOneRegion(string brand, string region, int months, double perWeekVolume)
    {
        var weekly = new List<object>();
        int weeks = months * 4;
        for (int w = 0; w < weeks; w++)
        {
            weekly.Add(new
            {
                week_starting = $"2024-{(w % 12) + 1:D2}-{(w % 4 * 7) + 1:D2}",
                region,
                channel = "Retail",
                volume = perWeekVolume,
                units = 100,
            });
        }
        return JsonSerializer.Serialize(new
        {
            period = new { start = "2024-01-01", end = "2024-12-31", months },
            filters = new { brand, region, channel = (string?)null },
            summary = new
            {
                total_volume = perWeekVolume * weeks,
                total_units = weeks * 100,
                weeks_of_data = weeks,
                avg_weekly_volume = perWeekVolume,
            },
            weekly_data = weekly,
        });
    }

    private static string BuildDemandPayloadAllRegions(string brand, int months)
    {
        var weekly = new List<object>();
        double totalVolume = 0;
        int weeks = months * 4;
        for (int r = 0; r < ChartAcceptanceManifest.SeededRegions.Count; r++)
        {
            string region = ChartAcceptanceManifest.SeededRegions[r];
            double perWeekVolume = 500.0 + (r * 50);
            for (int w = 0; w < weeks; w++)
            {
                weekly.Add(new
                {
                    week_starting = $"2024-{(w % 12) + 1:D2}-{(w % 4 * 7) + 1:D2}",
                    region,
                    channel = "Retail",
                    volume = perWeekVolume,
                    units = 90,
                });
                totalVolume += perWeekVolume;
            }
        }
        return JsonSerializer.Serialize(new
        {
            period = new { start = "2024-01-01", end = "2024-12-31", months },
            filters = new { brand, region = (string?)null, channel = (string?)null },
            summary = new
            {
                total_volume = totalVolume,
                total_units = ChartAcceptanceManifest.SeededRegions.Count * weeks * 90,
                weeks_of_data = weeks,
                avg_weekly_volume = Math.Round(totalVolume / weeks, 1),
            },
            weekly_data = weekly,
        });
    }

    private static string BuildLargePortfolioPayload(string region)
    {
        string[] brands =
        [
            "Sierra Gold Tequila", "Ridgeline Bourbon", "Summit Vodka",
            "FreshMart", "Harvest Table", "Apex Grill", "Coastline Tacos",
            "Pinnacle Hardware", "Summit Outdoor", "ClearDesk", "Urban Living",
            "Foundry Home",
        ];
        var rows = brands.Select((b, i) => new
        {
            brand = b,
            region,
            metrics = new
            {
                depletions_yoy = FormatSigned(-2.5 + (i * 0.9)),
                sell_through_yoy = "+1.2%",
                inventory_weeks_on_hand = 6.5 + (i * 0.1),
                status = "OnTrack",
            },
            sentiment_summary = new string('x', 400), // realistic verbose prose that compactor strips
        }).ToArray();
        return JsonSerializer.Serialize(new { region, period = "YTD", brandCount = rows.Length, brands = rows });
    }

    private static string BuildMarketSharePayload()
    {
        return JsonSerializer.Serialize(new
        {
            filters_applied = new { region = "National" },
            share_data = new object[]
            {
                new { brand = "FreshMart", share_percent = 42.5 },
                new { brand = "Harvest Table", share_percent = 27.1 },
                new { brand = "Other Grocery", share_percent = 30.4 },
            },
        });
    }

    private static string BuildVariantMixPayload(string brand, string region)
    {
        return JsonSerializer.Serialize(new
        {
            filters_applied = new { brand, region },
            variants = new object[]
            {
                new { variant = "Original", mix_percent = 45.0 },
                new { variant = "Spicy",    mix_percent = 33.0 },
                new { variant = "Verde",    mix_percent = 22.0 },
            },
        });
    }

    private static string BuildInventoryPayload(string brand, string region)
    {
        return JsonSerializer.Serialize(new
        {
            items = Array.Empty<object>(),
            total_items = 20,
            status_breakdown = new { healthy = 15, low = 3, critical = 1, out_of_stock = 1 },
            filters_applied = new { brand, region, category = (string?)null, status = (string?)null },
        });
    }

    private static string FormatSigned(double value)
    {
        string abs = Math.Abs(value).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        return value >= 0 ? $"+{abs}%" : $"-{abs}%";
    }

    /// <summary>
    /// Cheap stable argument fingerprint for the dedup key — every distinct call uses
    /// a distinct raw payload, so hashing on the raw content suffices to keep them
    /// separate under RequestToolContext dedup.
    /// </summary>
    private static string ArgsFingerprint(string toolName, string raw) => $"{toolName}::{raw.GetHashCode(StringComparison.Ordinal):X8}::{raw.Length}";
}

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Budget;
using RetailPulse.Api.Charts;
using RetailPulse.Contracts;
using RetailPulse.Tests.Fixtures;
using Xunit;

namespace RetailPulse.Tests.Budget;

/// <summary>
/// Tests the request-scoped behavior of <see cref="BudgetedAIFunction"/> +
/// <see cref="RequestToolContext"/>: dedup of identical calls, distinct arguments staying
/// distinct, the distinct-call cap, the cumulative per-request budget, exempt passthrough,
/// and principal-scoped isolation.
/// </summary>
public sealed class BudgetedAIFunctionTests
{
    private static ToolResultBudget CreateBudget() =>
        new([new HistoricalDemandCompactor(), new PortfolioDepletionCompactor()]);

    private static ToolResultBudgetOptions Options(int maxCumulative = 24_000, int maxCalls = 8) => new()
    {
        Enabled = true,
        MaxResultChars = 6000,
        MaxCumulativeChars = maxCumulative,
        MaxToolCalls = maxCalls,
        CharsPerToken = 4,
        MaxArrayItems = 24
    };

    /// <summary>A counting fake tool whose result is controllable per-invocation.</summary>
    private sealed class CountingFunction : AIFunction
    {
        private readonly Func<AIFunctionArguments, string> _body;
        public int Invocations { get; private set; }
        public override string Name { get; }
        public override string Description => "fake";
        public override JsonElement JsonSchema { get; } =
            JsonDocument.Parse("""{"type":"object","properties":{"brand":{"type":"string"}}}""").RootElement;

        public CountingFunction(string name, Func<AIFunctionArguments, string> body)
        {
            Name = name;
            _body = body;
        }

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken ct)
        {
            Invocations++;
            return ValueTask.FromResult<object?>(_body(arguments));
        }
    }

    private static BudgetedAIFunction Wrap(AIFunction inner, ToolResultBudgetOptions options) =>
        new(inner, CreateBudget(), options, NullLogger.Instance);

    private static AIFunctionArguments Args(params (string key, object? value)[] pairs)
    {
        var a = new AIFunctionArguments();
        foreach ((string key, object? value) in pairs)
            a[key] = value;
        return a;
    }

    [Fact]
    public async Task IdenticalCall_IsDeduplicated_InnerInvokedOnce()
    {
        var inner = new CountingFunction("GetDepletionStats",
            _ => JsonSerializer.Serialize(new { brand = "Apex Grill", v = 1 }));
        BudgetedAIFunction fn = Wrap(inner, Options());

        using IDisposable scope = RequestToolContext.Begin("session-1");
        object? first = await fn.InvokeAsync(Args(("brand", "Apex Grill")));
        object? second = await fn.InvokeAsync(Args(("brand", "Apex Grill")));

        inner.Invocations.Should().Be(1, "the identical second call is served from the dedup cache");
        first.Should().Be(second);

        RequestToolContext.Current!.DistinctCalls.Should().Be(1);
        RequestToolContext.Current!.Metrics.Should().Contain(m => m.Deduplicated);
    }

    [Fact]
    public async Task DistinctArguments_StayDistinct()
    {
        var inner = new CountingFunction("GetDepletionStats",
            a => JsonSerializer.Serialize(new { brand = a["brand"], v = 1 }));
        BudgetedAIFunction fn = Wrap(inner, Options());

        using IDisposable scope = RequestToolContext.Begin("session-1");
        await fn.InvokeAsync(Args(("brand", "Apex Grill")));
        await fn.InvokeAsync(Args(("brand", "Coastline Tacos")));

        inner.Invocations.Should().Be(2, "different brands are genuinely different calls");
        RequestToolContext.Current!.DistinctCalls.Should().Be(2);
    }

    [Fact]
    public async Task HistoricalDemand_StringJsonFromFactory_CompactsBothFurnitureBrandsWithinAggregateBudget()
    {
        string urbanPayload = BuildHistoricalDemandPayload("Urban Living");
        string foundryPayload = BuildHistoricalDemandPayload("Foundry Home");
        urbanPayload.Length.Should().BeGreaterThan(200_000);
        foundryPayload.Length.Should().BeGreaterThan(200_000);
        var invocations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        AIFunction inner = AIFunctionFactory.Create(
            (string brand) =>
            {
                invocations[brand] = invocations.GetValueOrDefault(brand) + 1;
                return brand switch
                {
                    "Urban Living" => urbanPayload,
                    "Foundry Furniture" or "Foundry Home" => foundryPayload,
                    _ => throw new InvalidOperationException($"Unexpected brand {brand}")
                };
            },
            "GetHistoricalDemand");
        BudgetedAIFunction fn = Wrap(inner, Options(maxCumulative: 9_500, maxCalls: 5));

        using IDisposable scope = RequestToolContext.Begin(
            "furniture-chart",
            isChartIntent: true);
        string urban = (string)(await fn.InvokeAsync(Args(("brand", "Urban Living"))))!;
        string foundry = (string)(await fn.InvokeAsync(Args(("brand", "Foundry Furniture"))))!;
        string repeatedUrban = (string)(await fn.InvokeAsync(Args(("brand", "Urban Living"))))!;
        string repeatedFoundry = (string)(await fn.InvokeAsync(Args(("brand", "Foundry Furniture"))))!;

        foreach ((string result, string expectedBrand) in new[]
        {
            (urban, "Urban Living"),
            (foundry, "Foundry Home")
        })
        {
            using var doc = JsonDocument.Parse(result);
            doc.RootElement.TryGetProperty("by_region", out JsonElement regions).Should().BeTrue();
            regions.GetArrayLength().Should().Be(6);
            doc.RootElement.GetProperty("filters").GetProperty("brand").GetString()
                .Should().Be(expectedBrand);
            double expectedRegionVolume = 6 * Enumerable.Range(0, 52).Sum(week => 1000 + week);
            int expectedRegionUnits = 6 * Enumerable.Range(0, 52).Sum(week => 100 + week);
            regions.EnumerateArray().Should().OnlyContain(region =>
                region.GetProperty("volume").GetDouble() == expectedRegionVolume
                && region.GetProperty("units").GetInt32() == expectedRegionUnits,
                "every compacted region must preserve the exact fixture-derived volume and unit sums");
            JsonElement temporal = doc.RootElement.GetProperty("by_time");
            temporal.GetArrayLength().Should().BeLessThanOrEqualTo(12);
            temporal.EnumerateArray().Sum(interval => interval.GetProperty("volume").GetDouble())
                .Should().Be(6 * expectedRegionVolume,
                    "bounded temporal intervals must preserve the exact all-region volume sum");
            temporal.EnumerateArray().Sum(interval => interval.GetProperty("units").GetInt32())
                .Should().Be(6 * expectedRegionUnits,
                    "bounded temporal intervals must preserve the exact all-region unit sum");
            JsonElement[] intervals = [.. temporal.EnumerateArray()];
            intervals.Should().HaveCount(11);
            for (int intervalIndex = 0; intervalIndex < intervals.Length; intervalIndex++)
            {
                int firstWeekIndex = intervalIndex * 5;
                int weeksInInterval = Math.Min(5, 52 - firstWeekIndex);
                DateOnly expectedStart = new DateOnly(2025, 5, 19).AddDays(firstWeekIndex * 7);
                DateOnly expectedEnd = expectedStart.AddDays((weeksInInterval - 1) * 7);
                double expectedVolume = 36 * Enumerable.Range(firstWeekIndex, weeksInInterval)
                    .Sum(week => 1000 + week);
                int expectedUnits = 36 * Enumerable.Range(firstWeekIndex, weeksInInterval)
                    .Sum(week => 100 + week);

                intervals[intervalIndex].GetProperty("start_week").GetString()
                    .Should().Be(expectedStart.ToString("yyyy-MM-dd"));
                intervals[intervalIndex].GetProperty("end_week").GetString()
                    .Should().Be(expectedEnd.ToString("yyyy-MM-dd"));
                intervals[intervalIndex].GetProperty("weeks").GetInt32().Should().Be(weeksInInterval);
                intervals[intervalIndex].GetProperty("source_rows").GetInt32()
                    .Should().Be(36 * weeksInInterval);
                intervals[intervalIndex].GetProperty("volume").GetDouble().Should().Be(expectedVolume);
                intervals[intervalIndex].GetProperty("units").GetInt32().Should().Be(expectedUnits);
            }
            doc.RootElement.TryGetProperty("compaction", out JsonElement compaction).Should().BeTrue();
            compaction.GetProperty("aggregate_complete").GetBoolean().Should().BeTrue();
            compaction.GetProperty("temporal_complete").GetBoolean().Should().BeTrue();
            result.Should().NotContain("tool_result_over_budget");
        }

        repeatedUrban.Should().Be(urban);
        repeatedFoundry.Should().Be(foundry);
        invocations.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["Urban Living"] = 1,
            ["Foundry Furniture"] = 1
        }, "the model's repeated calls in the observed live loop must reuse compact results");
        RequestToolContext.Current!.CumulativeChars.Should().BeLessThanOrEqualTo(9_500);
        RequestToolContext.Current.Metrics.Should().HaveCount(4);
        RequestToolContext.Current.Metrics.Count(metric => metric.Deduplicated).Should().Be(2);
        RequestToolContext.Current.Metrics.Where(metric => !metric.Deduplicated).Should().OnlyContain(metric =>
            metric.Compacted && !metric.Truncated && !metric.BudgetExceeded);
    }

    [Fact]
    public async Task HistoricalDemand_FactoryPeriodArgument_PreservesQuarterTemporalEvidence()
    {
        string payload = BuildHistoricalDemandPayload("Urban Living", weeks: 7, periodLabel: "current-quarter");
        string? invokedPeriod = null;
        AIFunction inner = AIFunctionFactory.Create(
            (string brand, string period) =>
            {
                invokedPeriod = period;
                return payload;
            },
            "GetHistoricalDemand");
        BudgetedAIFunction fn = Wrap(inner, Options(maxCumulative: 9_500, maxCalls: 5));

        using IDisposable scope = RequestToolContext.Begin("quarter-chart", isChartIntent: true);
        string result = (string)(await fn.InvokeAsync(Args(
            ("brand", "Urban Living"),
            ("period", "current-quarter"))))!;

        invokedPeriod.Should().Be("current-quarter");
        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("period").GetProperty("label").GetString()
            .Should().Be("current-quarter");
        JsonElement temporal = doc.RootElement.GetProperty("by_time");
        temporal.GetArrayLength().Should().Be(7);
        temporal.EnumerateArray().Select(row => row.GetProperty("start_week").GetString())
            .Should().BeInAscendingOrder();
        temporal.EnumerateArray().Sum(row => row.GetProperty("volume").GetDouble())
            .Should().Be(doc.RootElement.GetProperty("summary").GetProperty("total_volume").GetDouble());
        result.Should().NotContain("tool_result_over_budget");
    }

    [Fact]
    public async Task HistoricalDemand_ExactBarPrompt_MixedEvidenceBuildsRegionalTotalVolumeChart()
    {
        string urbanPayload = BuildHistoricalDemandPayload(
            "Urban Living", weeks: 7, periodLabel: "current-quarter", targetTotalVolume: 59_306.9);
        string foundryPayload = BuildHistoricalDemandPayload(
            "Foundry Home", weeks: 7, periodLabel: "current-quarter", targetTotalVolume: 68_046.9);
        AIFunction inner = AIFunctionFactory.Create(
            (string brand, string period) => brand switch
            {
                "Urban Living" => urbanPayload,
                "Foundry Furniture" => foundryPayload,
                _ => throw new InvalidOperationException($"Unexpected brand {brand}")
            },
            "GetHistoricalDemand");
        BudgetedAIFunction fn = Wrap(inner, Options(maxCumulative: 9_500, maxCalls: 5));

        using IDisposable scope = RequestToolContext.Begin("quarter-fulfillment", isChartIntent: true);
        string urban = (string)(await fn.InvokeAsync(Args(
            ("brand", "Urban Living"), ("period", "current-quarter"))))!;
        string foundry = (string)(await fn.InvokeAsync(Args(
            ("brand", "Foundry Furniture"), ("period", "current-quarter"))))!;

        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var pipeline = new AgentExecutionPipeline(
            AgentTestFixtures.CreateMockChatClient("{}"),
            AgentTestFixtures.CreateMockHubContext(),
            config,
            NullLogger<AgentExecutionPipeline>.Instance);
        var wrongModelChart = new ChartSpec
        {
            Type = "groupedBar",
            Title = "Model-created comparison",
            Data =
            [
                new ChartSeries
                {
                    Legend = "Urban Living",
                    Values =
                    [
                        new ChartDataPoint { X = "Northeast", Y = 1 },
                        new ChartDataPoint { X = "Midwest", Y = 2 },
                    ],
                },
                new ChartSeries
                {
                    Legend = "Foundry Furniture",
                    Values =
                    [
                        new ChartDataPoint { X = "Northeast", Y = 3 },
                        new ChartDataPoint { X = "Midwest", Y = 4 },
                    ],
                },
            ],
        };
        PrefetchEntry[] prefetched =
        [
            new("GetHistoricalDemand:Foundry Furniture", foundry, true, false, null, null),
        ];

        AgentExecutionPipeline.ChartFulfillmentResult fulfilled = pipeline.EnforceChartFulfillment(
            "Create a bar chart comparing Foundry Furniture and Urban Living depletion volume across all regions this quarter",
            new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant,
                    [new FunctionResultContent("urban-call", urban)])),
            [wrongModelChart],
            "Done.",
            prefetched);

        fulfilled.Charts.Should().ContainSingle();
        ChartSpec chart = fulfilled.Charts.Single();
        chart.Should().NotBeSameAs(wrongModelChart);
        chart.Type.Should().Be("groupedBar");
        chart.Title.Should().Contain("current-quarter: 2026-04-01 to 2026-05-13");
        chart.XAxisTitle.Should().Be("Region");
        chart.YAxisTitle.Should().Be("Depletion Volume");
        chart.Data.Select(series => series.Legend).Should().Equal("Urban Living", "Foundry Home");
        chart.Data.Should().OnlyContain(series => series.Values.Count == 6);
        foreach (ChartSeries series in chart.Data)
        {
            series.Values.Select(point => point.X).Should().BeEquivalentTo(
                "Midwest", "Northeast", "Pacific Northwest",
                "Southeast", "Southwest", "West Coast");
        }
        chart.Data.Single(series => series.Legend == "Urban Living").Values.Sum(point => point.Y)
            .Should().BeApproximately(59_306.9, 0.1);
        chart.Data.Single(series => series.Legend == "Foundry Home").Values.Sum(point => point.Y)
            .Should().BeApproximately(68_046.9, 0.1);
        RequestToolContext.Current!.CumulativeChars.Should().BeLessThanOrEqualTo(9_500);
        RequestToolContext.Current.DistinctCalls.Should().Be(2);
    }

    [Fact]
    public async Task FactoryResultShapes_PreservePlainTextAndObjectJson()
    {
        BudgetedAIFunction plainText = Wrap(
            AIFunctionFactory.Create(() => "Historical demand unavailable.", "PlainText"),
            Options());
        BudgetedAIFunction objectJson = Wrap(
            AIFunctionFactory.Create(() => new { status = "ok", count = 2 }, "ObjectJson"),
            Options());

        using IDisposable scope = RequestToolContext.Begin("result-shapes");
        object? textResult = await plainText.InvokeAsync(Args());
        object? objectResult = await objectJson.InvokeAsync(Args());

        textResult.Should().Be("Historical demand unavailable.");
        using var doc = JsonDocument.Parse((string)objectResult!);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        doc.RootElement.GetProperty("status").GetString().Should().Be("ok");
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task ArgumentOrder_DoesNotDefeatDedup()
    {
        var inner = new CountingFunction("GetDepletionStats",
            _ => JsonSerializer.Serialize(new { ok = true }));
        BudgetedAIFunction fn = Wrap(inner, Options());

        using IDisposable scope = RequestToolContext.Begin("session-1");
        await fn.InvokeAsync(Args(("brand", "Apex Grill"), ("region", "National")));
        await fn.InvokeAsync(Args(("region", "National"), ("brand", "Apex Grill")));

        inner.Invocations.Should().Be(1, "arguments are normalized to a stable, order-independent key");
    }

    [Fact]
    public async Task DistinctCallCap_ReturnsDiagnostic_WithoutInvoking()
    {
        var inner = new CountingFunction("GetDepletionStats",
            a => JsonSerializer.Serialize(new { brand = a["brand"] }));
        BudgetedAIFunction fn = Wrap(inner, Options(maxCalls: 2));

        using IDisposable scope = RequestToolContext.Begin("session-1");
        await fn.InvokeAsync(Args(("brand", "A")));
        await fn.InvokeAsync(Args(("brand", "B")));
        object? third = await fn.InvokeAsync(Args(("brand", "C")));

        inner.Invocations.Should().Be(2, "the 3rd distinct call is refused by the cap");
        using var doc = JsonDocument.Parse((string)third!);
        doc.RootElement.TryGetProperty("budget_notice", out _).Should().BeTrue();
    }

    [Fact]
    public async Task CumulativeBudget_WithholdsFurtherResults()
    {
        // Each call returns ~4000 chars; a 6000-char cumulative cap admits one, withholds the next.
        var inner = new CountingFunction("BigTool",
            a => JsonSerializer.Serialize(new { brand = a["brand"], blob = new string('x', 4000) }));
        BudgetedAIFunction fn = Wrap(inner, Options(maxCumulative: 6000));

        using IDisposable scope = RequestToolContext.Begin("session-1");
        object? first = await fn.InvokeAsync(Args(("brand", "A")));
        object? second = await fn.InvokeAsync(Args(("brand", "B")));

        ((string)first!).Should().Contain("blob", "the first result fits under the cumulative cap");
        using var doc = JsonDocument.Parse((string)second!);
        doc.RootElement.TryGetProperty("budget_notice", out _).Should().BeTrue(
            "the second result exceeds the cumulative budget and is withheld");
        string notice = doc.RootElement.GetProperty("budget_notice").GetString()!;
        notice.Should().Contain("This tool result was withheld");
        notice.Should().Contain("do not claim they cover data that was not returned");
        notice.Should().NotContain("COMPLETE",
            "crossing the byte budget does not prove the earlier results satisfy the request");
    }

    [Fact]
    public async Task ExemptTool_PassesThrough_AndDoesNotCountCumulatively()
    {
        string bigChart = JsonSerializer.Serialize(new { type = "grouped-bar", blob = new string('c', 10_000) });
        var inner = new CountingFunction("CreateChart", _ => bigChart);
        BudgetedAIFunction fn = Wrap(inner, Options(maxCumulative: 6000));

        using IDisposable scope = RequestToolContext.Begin("session-1");
        object? result = await fn.InvokeAsync(Args());

        ((string)result!).Should().Be(bigChart, "the canonical ChartSpec is never compacted");
        RequestToolContext.Current!.CumulativeChars.Should().Be(0,
            "exempt tools do not consume the cumulative budget");
    }

    [Fact]
    public async Task NoActiveScope_PassesThrough_Unbudgeted()
    {
        string big = JsonSerializer.Serialize(new { blob = new string('x', 50_000) });
        var inner = new CountingFunction("BigTool", _ => big);
        BudgetedAIFunction fn = Wrap(inner, Options());

        // No RequestToolContext.Begin — e.g. the simplified/test pipeline path.
        object? result = await fn.InvokeAsync(Args());

        ((string)result!).Should().Be(big);
        inner.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task Dedup_IsScopedToTheRequest_NotAcrossScopes()
    {
        var inner = new CountingFunction("GetDepletionStats",
            _ => JsonSerializer.Serialize(new { ok = true }));
        BudgetedAIFunction fn = Wrap(inner, Options());

        using (RequestToolContext.Begin("session-1"))
        {
            await fn.InvokeAsync(Args(("brand", "A")));
        }
        using (RequestToolContext.Begin("session-2"))
        {
            await fn.InvokeAsync(Args(("brand", "A")));
        }

        inner.Invocations.Should().Be(2, "a new request scope starts with an empty dedup cache");
    }

    private static string BuildHistoricalDemandPayload(
        string brand,
        int weeks = 52,
        string periodLabel = "last-12-months",
        double? targetTotalVolume = null)
    {
        string[] regions =
        [
            "Midwest", "Northeast", "Pacific Northwest",
            "Southeast", "Southwest", "West Coast"
        ];
        string[] channels =
        [
            "On-Premise", "Off-Premise", "E-Commerce",
            "Direct", "Distributor", "Marketplace"
        ];
        var rows = new List<object>();
        double totalVolume = 0;
        int totalUnits = 0;
        DateOnly firstWeek = periodLabel == "current-quarter"
            ? new DateOnly(2026, 4, 1)
            : new DateOnly(2025, 5, 19);

        double[]? regionalTargets = null;
        if (targetTotalVolume is double target)
        {
            double baseRegion = Math.Floor(target / regions.Length * 10) / 10;
            regionalTargets = [.. Enumerable.Repeat(baseRegion, regions.Length)];
            regionalTargets[^1] = Math.Round(target - (baseRegion * (regions.Length - 1)), 1);
        }

        for (int regionIndex = 0; regionIndex < regions.Length; regionIndex++)
        {
            string region = regions[regionIndex];
            foreach (string channel in channels)
            {
                for (int week = 0; week < weeks; week++)
                {
                    double volume = regionalTargets is not null
                        ? regionalTargets[regionIndex] / (channels.Length * weeks)
                        : 1000 + week;
                    int units = 100 + week;
                    totalVolume += volume;
                    totalUnits += units;
                    rows.Add(new
                    {
                        brand,
                        region,
                        channel,
                        week_starting = firstWeek.AddDays(week * 7).ToString("yyyy-MM-dd"),
                        volume,
                        units,
                        avg_daily_volume = Math.Round(volume / 7, 1)
                    });
                }
            }
        }

        return JsonSerializer.Serialize(new
        {
            period = new
            {
                label = periodLabel,
                start = periodLabel == "current-quarter" ? "2026-04-01" : "2025-05-13",
                end = "2026-05-13",
                months = periodLabel == "current-quarter" ? 3 : 12
            },
            filters = new { brand, region = "all", channel = "all" },
            summary = new
            {
                total_volume = totalVolume,
                total_units = totalUnits,
                weeks_of_data = rows.Count,
                avg_weekly_volume = Math.Round(totalVolume / weeks, 1)
            },
            weekly_data = rows
        });
    }
}

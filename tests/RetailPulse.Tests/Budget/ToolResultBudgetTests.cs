using System.Text.Json;
using FluentAssertions;
using RetailPulse.Api.Budget;
using Xunit;

namespace RetailPulse.Tests.Budget;

/// <summary>
/// Unit tests for the <see cref="ToolResultBudget"/> compaction orchestrator and its
/// compactors. These are deterministic and do not touch a model or the database.
/// </summary>
public sealed class ToolResultBudgetTests
{
    private static ToolResultBudget CreateBudget() =>
        new(
        [
            new HistoricalDemandCompactor(),
            new PortfolioDepletionCompactor()
        ]);

    private static ToolResultBudgetOptions Options(int maxResult = 6000, int maxArrayItems = 24) => new()
    {
        Enabled = true,
        MaxResultChars = maxResult,
        MaxCumulativeChars = 24_000,
        MaxToolCalls = 8,
        CharsPerToken = 4,
        MaxArrayItems = maxArrayItems
    };

    [Fact]
    public void UnderBudget_PassesThrough_Unchanged()
    {
        ToolResultBudget budget = CreateBudget();
        string raw = JsonSerializer.Serialize(new { brand = "Apex Grill", value = 42 });

        BudgetedResult result = budget.Apply("GetDepletionStats", raw, Options());

        result.Json.Should().Be(raw);
        result.Metrics.Compacted.Should().BeFalse();
        result.Metrics.Truncated.Should().BeFalse();
        result.Metrics.OriginalChars.Should().Be(raw.Length);
        result.Metrics.ReturnedChars.Should().Be(raw.Length);
    }

    [Fact]
    public void ExemptTool_IsNeverCompacted_EvenWhenHuge()
    {
        ToolResultBudget budget = CreateBudget();
        // A large CreateChart payload (canonical ChartSpec) must pass through untouched.
        string bigChart = JsonSerializer.Serialize(new
        {
            type = "grouped-bar",
            title = "Depletions",
            data = Enumerable.Range(0, 500).Select(i => new { legend = $"S{i}", values = new[] { new { x = "R", y = i } } })
        });
        bigChart.Length.Should().BeGreaterThan(6000);

        BudgetedResult result = budget.Apply("CreateChart", bigChart, Options());

        result.Json.Should().Be(bigChart);
        result.Metrics.Exempt.Should().BeTrue();
        result.Metrics.Compacted.Should().BeFalse();
        result.Metrics.Truncated.Should().BeFalse();
    }

    [Fact]
    public void HistoricalDemand_IsRolledUp_PreservingSummary()
    {
        ToolResultBudget budget = CreateBudget();
        string raw = BuildHistoricalDemand(regions: ["West", "East", "South"], weeksPerRegion: 52);
        raw.Length.Should().BeGreaterThan(6000);

        BudgetedResult result = budget.Apply("GetHistoricalDemand", raw, Options());

        result.Json.Length.Should().BeLessThan(raw.Length);
        result.Metrics.Compacted.Should().BeTrue();

        using var doc = JsonDocument.Parse(result.Json);
        JsonElement root = doc.RootElement;

        // Canonical summary is preserved verbatim.
        root.TryGetProperty("summary", out JsonElement summary).Should().BeTrue();
        summary.TryGetProperty("total_volume", out _).Should().BeTrue();

        // Weekly rows are replaced by an aligned per-region rollup.
        root.TryGetProperty("by_region", out JsonElement byRegion).Should().BeTrue();
        byRegion.GetArrayLength().Should().Be(3);

        // Explicit, honest compaction metadata.
        root.TryGetProperty("compaction", out JsonElement compaction).Should().BeTrue();
        compaction.GetProperty("compacted").GetBoolean().Should().BeTrue();
        compaction.GetProperty("original_weekly_rows").GetInt32().Should().Be(3 * 52);
    }

    [Fact]
    public void HistoricalDemand_RollupVolumes_AreFaithfulSums()
    {
        // Exercise the tool-specific compactor directly so the projection is asserted
        // independent of the orchestrator's fallback thresholds.
        var compactor = new HistoricalDemandCompactor();
        // West: two weeks, volumes 100 + 200 = 300; East: one week, 50.
        string raw = JsonSerializer.Serialize(new
        {
            period = new { start = "2024-01-01", end = "2024-12-31", months = 12 },
            filters = new { brand = "Apex Grill", region = (string?)null, channel = (string?)null },
            summary = new { total_volume = 350.0, total_units = 35, weeks_of_data = 2, avg_weekly_volume = 175.0 },
            weekly_data = new object[]
            {
                new { brand = "Apex Grill", region = "West", channel = "Retail", week_starting = "2024-01-01", volume = 100.0, units = 10, avg_daily_volume = 14.3 },
                new { brand = "Apex Grill", region = "West", channel = "Retail", week_starting = "2024-01-08", volume = 200.0, units = 20, avg_daily_volume = 28.6 },
                new { brand = "Apex Grill", region = "East", channel = "Retail", week_starting = "2024-01-01", volume = 50.0, units = 5, avg_daily_volume = 7.1 }
            }
        });

        ToolCompactionOutcome outcome = compactor.Compact("GetHistoricalDemand", raw, Options());
        outcome.Changed.Should().BeTrue();

        using var doc = JsonDocument.Parse(outcome.Json);
        JsonElement byRegion = doc.RootElement.GetProperty("by_region");
        var volumes = byRegion.EnumerateArray()
            .ToDictionary(e => e.GetProperty("region").GetString()!, e => e.GetProperty("volume").GetDouble());

        volumes["West"].Should().Be(300.0);
        volumes["East"].Should().Be(50.0);
    }

    [Fact]
    public void PortfolioDepletion_DropsSentimentNarrative_KeepsMetrics()
    {
        ToolResultBudget budget = CreateBudget();
        string raw = JsonSerializer.Serialize(new
        {
            region = "National",
            period = "YTD",
            brandCount = 2,
            brands = new object[]
            {
                new
                {
                    brand = "Apex Grill",
                    region = "National",
                    metrics = new { depletions_yoy = "+3.2%", sell_through_yoy = "-1.0%", inventory_weeks_on_hand = 7.5, status = "Healthy" },
                    sentiment_summary = new string('x', 5000)
                },
                new
                {
                    brand = "Coastline Tacos",
                    region = "National",
                    metrics = new { depletions_yoy = "+1.1%", sell_through_yoy = "-2.0%", inventory_weeks_on_hand = 9.0, status = "Overstocked" },
                    sentiment_summary = new string('y', 5000)
                }
            }
        });
        raw.Length.Should().BeGreaterThan(6000);

        BudgetedResult result = budget.Apply("GetPortfolioDepletionStats", raw, Options());

        result.Metrics.Compacted.Should().BeTrue();
        result.Json.Length.Should().BeLessThan(raw.Length);

        using var doc = JsonDocument.Parse(result.Json);
        JsonElement brands = doc.RootElement.GetProperty("brands");
        brands.GetArrayLength().Should().Be(2);
        JsonElement first = brands[0];
        first.GetProperty("brand").GetString().Should().Be("Apex Grill");
        first.GetProperty("depletions_yoy").GetString().Should().Be("+3.2%");
        first.TryGetProperty("sentiment_summary", out _).Should().BeFalse("verbose narrative is dropped");
    }

    [Fact]
    public void GenericArrayCompactor_TrimsAndAnnotates_ForUnknownTool()
    {
        ToolResultBudget budget = CreateBudget();
        string raw = JsonSerializer.Serialize(new
        {
            rows = Enumerable.Range(0, 500).Select(i => new { id = i, note = $"row-{i}-payload" })
        });
        raw.Length.Should().BeGreaterThan(6000);

        BudgetedResult result = budget.Apply("SomeUnknownTool", raw, Options(maxArrayItems: 10));

        result.Metrics.Truncated.Should().BeTrue();
        using var doc = JsonDocument.Parse(result.Json);
        JsonElement root = doc.RootElement;
        root.GetProperty("rows").GetArrayLength().Should().Be(10);
        root.TryGetProperty("_truncation", out JsonElement trunc).Should().BeTrue();
        trunc.GetProperty("truncated").GetBoolean().Should().BeTrue();
        trunc.GetProperty("original_count").GetInt32().Should().Be(500);
        trunc.GetProperty("returned_count").GetInt32().Should().Be(10);
    }

    [Fact]
    public void PathologicalString_IsHardClipped_ToValidJson()
    {
        ToolResultBudget budget = CreateBudget();
        // A giant non-array, non-object JSON string value: no array to trim → hard clip.
        string raw = JsonSerializer.Serialize(new string('z', 50_000));
        raw.Length.Should().BeGreaterThan(6000);

        BudgetedResult result = budget.Apply("WeirdTool", raw, Options());

        result.Json.Length.Should().BeLessThanOrEqualTo(6000);
        result.Metrics.Truncated.Should().BeTrue();
        // Still valid JSON with explicit metadata.
        using var doc = JsonDocument.Parse(result.Json);
        doc.RootElement.GetProperty("_budget").GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void MalformedJson_IsHardClipped_NeverThrows()
    {
        ToolResultBudget budget = CreateBudget();
        string raw = "{ this is : not valid json " + new string('!', 10_000);

        BudgetedResult result = budget.Apply("BrokenTool", raw, Options());

        // Bounded, and the envelope itself is valid JSON even though the input was not.
        result.Json.Length.Should().BeLessThanOrEqualTo(6000);
        Action parse = () => JsonDocument.Parse(result.Json).Dispose();
        parse.Should().NotThrow();
    }

    [Fact]
    public void Disabled_PassesEverythingThrough()
    {
        ToolResultBudget budget = CreateBudget();
        ToolResultBudgetOptions options = Options();
        options.Enabled = false;
        string raw = BuildHistoricalDemand(["West", "East"], 52);

        BudgetedResult result = budget.Apply("GetHistoricalDemand", raw, options);

        result.Json.Should().Be(raw);
        result.Metrics.Compacted.Should().BeFalse();
    }

    private static string BuildHistoricalDemand(string[] regions, int weeksPerRegion)
    {
        var weekly = new List<object>();
        foreach (string region in regions)
        {
            for (int w = 0; w < weeksPerRegion; w++)
            {
                weekly.Add(new
                {
                    brand = "Apex Grill",
                    region,
                    channel = "Retail",
                    week_starting = $"2024-W{w:00}",
                    volume = 100.0 + w,
                    units = 10 + w,
                    avg_daily_volume = 14.3
                });
            }
        }

        return JsonSerializer.Serialize(new
        {
            period = new { start = "2024-01-01", end = "2024-12-31", months = 12 },
            filters = new { brand = "Apex Grill", region = (string?)null, channel = (string?)null },
            summary = new { total_volume = 100000.0, total_units = 10000, weeks_of_data = weeksPerRegion, avg_weekly_volume = 1923.0 },
            weekly_data = weekly
        });
    }
}

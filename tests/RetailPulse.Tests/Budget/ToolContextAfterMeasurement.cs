using System.Text.Json;
using FluentAssertions;
using RetailPulse.Api.Budget;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;
using RetailPulse.Tests.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace RetailPulse.Tests.Budget;

/// <summary>
/// Deterministic BEFORE/AFTER measurement for the P0 baseline query
///   "Compare Coastline Tacos vs Apex Grill depletions across all regions".
///
/// Measures the exact JSON payloads each tool returns from the real seeded database,
/// then runs them through the <see cref="ToolResultBudget"/> boundary and asserts the
/// compacted tool-context footprint collapses far below the 25K-estimated-token gate —
/// while preserving the totals/summary a two-brand comparison chart needs.
///
/// This is the primary CI size gate. It does NOT call a live model.
/// </summary>
public sealed class ToolContextAfterMeasurement : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dbPath;
    private readonly RetailPulseDb _db;
    private readonly ToolResultBudget _budget;
    private readonly ToolResultBudgetOptions _options;

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    public ToolContextAfterMeasurement(ITestOutputHelper output)
    {
        _out = output;
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");
        _dbPath = SqliteTestCleanup.NewDbPath("budget_after");
        var tenantProvider = new FileTenantProvider(tenantConfigPath);
        _db = new RetailPulseDb(tenantProvider, _dbPath, tenantConfigPath);

        _budget = new ToolResultBudget(
        [
            new HistoricalDemandCompactor(),
            new PortfolioDepletionCompactor()
        ]);
        _options = new ToolResultBudgetOptions
        {
            Enabled = true,
            MaxResultChars = 6000,
            MaxCumulativeChars = 24_000,
            MaxToolCalls = 8,
            CharsPerToken = 4,
            MaxArrayItems = 24
        };
    }

    private static int EstTokens(int chars) => (int)Math.Ceiling(chars / 4.0);

    private (int beforeTokens, int afterTokens) Measure(string label, string toolName, object payload)
    {
        string raw = JsonSerializer.Serialize(payload, _json);
        BudgetedResult budgeted = _budget.Apply(toolName, raw, _options);
        int before = EstTokens(raw.Length);
        int after = EstTokens(budgeted.Json.Length);
        _out.WriteLine($"{label,-52} before_tok={before,7:N0}  after_tok={after,6:N0}  " +
                       $"compacted={budgeted.Metrics.Compacted} truncated={budgeted.Metrics.Truncated}");
        return (before, after);
    }

    [Fact]
    public void After_Compaction_TwoBrandComparison_IsUnderBudget()
    {
        _out.WriteLine("=== AFTER tool-context footprint (per single occurrence, compacted) ===");
        _out.WriteLine("Query: Compare Coastline Tacos vs Apex Grill depletions across all regions");
        _out.WriteLine("");

        int beforeTotal = 0, afterTotal = 0;

        void Add((int before, int after) m) { beforeTotal += m.before; afterTotal += m.after; }

        Add(Measure("GetDepletionStats(Coastline Tacos, National)", "GetDepletionStats",
            _db.GetDepletionStats("Coastline Tacos", "National", "YTD")));
        Add(Measure("GetDepletionStats(Apex Grill, National)", "GetDepletionStats",
            _db.GetDepletionStats("Apex Grill", "National", "YTD")));
        Add(Measure("GetPortfolioDepletionStats(National)", "GetPortfolioDepletionStats",
            _db.GetPortfolioDepletionStats("National", "YTD")));
        Add(Measure("GetHistoricalDemand(Coastline Tacos, 12mo)", "GetHistoricalDemand",
            _db.GetHistoricalDemand("Coastline Tacos", null, null, 12)));
        Add(Measure("GetHistoricalDemand(Apex Grill, 12mo)", "GetHistoricalDemand",
            _db.GetHistoricalDemand("Apex Grill", null, null, 12)));

        double reduction = beforeTotal == 0 ? 0 : 1.0 - ((double)afterTotal / beforeTotal);
        _out.WriteLine("");
        _out.WriteLine($"SUM before est_tokens = {beforeTotal:N0}");
        _out.WriteLine($"SUM after  est_tokens = {afterTotal:N0}");
        _out.WriteLine($"Reduction              = {reduction:P1}");

        // Primary CI gate: compacted single-occurrence tool context well under 25K tokens.
        afterTotal.Should().BeLessThan(25_000, "the compacted tool context must fit the budget");
        // And demonstrably far lower — assert an aggressive ceiling to catch regressions.
        afterTotal.Should().BeLessThan(6_000, "compaction should collapse the footprint by an order of magnitude");
        // At least an 80% reduction versus raw.
        reduction.Should().BeGreaterThanOrEqualTo(0.80, "target is >=80% token reduction");
    }

    [Fact]
    public void After_Compaction_PreservesComparisonEssentials()
    {
        // The compacted GetHistoricalDemand must still carry the totals + aligned per-region
        // points a two-brand grouped-bar comparison needs.
        string raw = JsonSerializer.Serialize(_db.GetHistoricalDemand("Apex Grill", null, null, 12), _json);
        BudgetedResult budgeted = _budget.Apply("GetHistoricalDemand", raw, _options);

        using var doc = JsonDocument.Parse(budgeted.Json);
        JsonElement root = doc.RootElement;

        root.TryGetProperty("summary", out JsonElement summary).Should().BeTrue();
        summary.TryGetProperty("total_volume", out _).Should().BeTrue();
        summary.TryGetProperty("total_units", out _).Should().BeTrue();
        summary.TryGetProperty("avg_weekly_volume", out _).Should().BeTrue();

        root.TryGetProperty("by_region", out JsonElement byRegion).Should().BeTrue();
        byRegion.GetArrayLength().Should().BeGreaterThan(0, "aligned per-region points remain for the chart");
        foreach (JsonElement r in byRegion.EnumerateArray())
        {
            r.TryGetProperty("region", out _).Should().BeTrue();
            r.TryGetProperty("volume", out _).Should().BeTrue();
        }
    }

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_dbPath);
}

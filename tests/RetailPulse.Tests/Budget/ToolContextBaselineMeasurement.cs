using System.Text.Json;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;
using Xunit;
using Xunit.Abstractions;
using RetailPulse.Tests.TestInfrastructure;

namespace RetailPulse.Tests.Budget;

/// <summary>
/// Deterministic measurement harness that quantifies the serialized tool-context
/// footprint for the P0 baseline query:
///   "Compare Coastline Tacos vs Apex Grill depletions across all regions".
///
/// This does NOT call a live model — it measures the exact JSON payloads the tools
/// return, which are the payloads that enter model context (once per tool call,
/// then re-sent on every subsequent function-invocation iteration). It prints
/// per-tool serialized characters + an estimated token count (chars / 4).
///
/// Run with:
///   dotnet test --filter FullyQualifiedName~ToolContextBaselineMeasurement
/// </summary>
public sealed class ToolContextBaselineMeasurement : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dbPath;
    private readonly RetailPulseDb _db;

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    public ToolContextBaselineMeasurement(ITestOutputHelper output)
    {
        _out = output;
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");
        _dbPath = SqliteTestCleanup.NewDbPath("budget_measure");
        var tenantProvider = new FileTenantProvider(tenantConfigPath);
        _db = new RetailPulseDb(tenantProvider, _dbPath, tenantConfigPath);
    }

    private static int EstTokens(int chars) => (int)Math.Ceiling(chars / 4.0);

    private (int chars, int tokens) Emit(string label, object payload)
    {
        string json = JsonSerializer.Serialize(payload, _json);
        int chars = json.Length;
        int tokens = EstTokens(chars);
        _out.WriteLine($"{label,-52} chars={chars,8:N0}  est_tokens={tokens,7:N0}");
        return (chars, tokens);
    }

    [Fact]
    public void Measure_Baseline_Depletion_Comparison_Query()
    {
        _out.WriteLine("=== BASELINE tool-context footprint (per single occurrence) ===");
        _out.WriteLine("Query: Compare Coastline Tacos vs Apex Grill depletions across all regions");
        _out.WriteLine("");

        int total = 0;

        // What the depletion comparison agent plausibly calls today.
        // 1) Per-brand depletion stats (National == all regions rollup).
        total += Emit("GetDepletionStats(Coastline Tacos, National)",
            _db.GetDepletionStats("Coastline Tacos", "National", "YTD")).tokens;
        total += Emit("GetDepletionStats(Apex Grill, National)",
            _db.GetDepletionStats("Apex Grill", "National", "YTD")).tokens;

        // 2) Portfolio-wide (all brands) — a common "across all" shortcut.
        total += Emit("GetPortfolioDepletionStats(National)",
            _db.GetPortfolioDepletionStats("National", "YTD")).tokens;

        _out.WriteLine("");
        _out.WriteLine("--- 'raw 12-month dataset' amplifier (prefetch / demand path) ---");
        // The prefetch service injects GetHistoricalDemand (all regions, all channels,
        // 12 months of weekly buckets) into the SYSTEM PROMPT when the query is
        // classified DemandForecasting — then re-sent on every iteration.
        total += Emit("GetHistoricalDemand(Coastline Tacos, all, all, 12mo)",
            _db.GetHistoricalDemand("Coastline Tacos", null, null, 12)).tokens;
        total += Emit("GetHistoricalDemand(Apex Grill, all, all, 12mo)",
            _db.GetHistoricalDemand("Apex Grill", null, null, 12)).tokens;

        _out.WriteLine("");
        _out.WriteLine($"SUM single-occurrence est_tokens = {total:N0}");
        _out.WriteLine("");
        _out.WriteLine("NOTE: FunctionInvokingChatClient re-sends the full accumulated");
        _out.WriteLine("message history (system prompt + every prior tool result) on each of");
        _out.WriteLine("MaximumIterationsPerRequest=3 iterations, so per-occurrence tokens are");
        _out.WriteLine("multiplied ~2-3x across the request. This is the amplification the");
        _out.WriteLine("ToolResultBudget boundary is designed to cap.");

        Assert.True(total > 0);
    }

    public void Dispose()
    {
        SqliteTestCleanup.ReleaseAndDelete(_dbPath);
    }
}

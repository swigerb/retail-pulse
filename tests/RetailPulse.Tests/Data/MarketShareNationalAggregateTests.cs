using System.Text.Json;
using FluentAssertions;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;
using Xunit;

namespace RetailPulse.Tests.Data;

/// <summary>
/// Publix production sweep #76 Group B — a chart-capable tool must always
/// produce an aggregate national/all-region rollup for prompts that ask for
/// data "nationally" or across all regions, so a pie/donut builder never
/// receives only per-region raw rows that the tool-context compactor
/// truncates before they can be assembled into a coherent breakdown.
///
/// The pie prompt (#21 "market share breakdown for our grocery brands
/// nationally") returned <c>charts: null</c> in prod because
/// <c>GetMarketShare(category: "Grocery")</c> emitted 6 regions × 6 quarters
/// × ~4 brands of raw rows that the generic array compactor truncated to 24
/// items, leaving the deterministic pie builder with a mangled slice.
/// </summary>
public sealed class MarketShareNationalAggregateTests : IDisposable
{
    private readonly string _dbPath;
    private readonly RetailPulseDb _db;

    public MarketShareNationalAggregateTests()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");

        _dbPath = Path.Combine(Path.GetTempPath(), $"retailpulse_msnat_test_{Guid.NewGuid():N}.db");
        var tenantProvider = new FileTenantProvider(tenantConfigPath);
        _db = new RetailPulseDb(tenantProvider, _dbPath, tenantConfigPath);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private static JsonElement Parse(object obj) =>
        JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement.Clone();

    [Fact]
    public void GetMarketShare_NoRegion_ExposesPerBrandNationalAggregate()
    {
        JsonElement result = Parse(_db.GetMarketShare(category: "Spirits"));

        result.TryGetProperty("national_share", out JsonElement national).Should().BeTrue(
            "national_share must be present when no region filter is supplied");
        national.ValueKind.Should().Be(JsonValueKind.Object);
        national.TryGetProperty("entries", out JsonElement entries).Should().BeTrue();
        entries.ValueKind.Should().Be(JsonValueKind.Array);

        var brands = entries.EnumerateArray()
            .Select(e => e.GetProperty("brand").GetString())
            .Where(b => !string.IsNullOrEmpty(b))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        brands.Count.Should().BeGreaterThanOrEqualTo(2,
            "an aggregate breakdown needs at least two brands to draw a pie");

        foreach (JsonElement entry in entries.EnumerateArray())
        {
            entry.TryGetProperty("share_percent", out JsonElement pct).Should().BeTrue();
            pct.GetDouble().Should().BeInRange(0, 100,
                "share_percent must sit in [0,100] for a bounded pie axis");
        }
    }

    [Fact]
    public void GetMarketShare_NoRegion_Grocery_YieldsBrandsSuitableForNationalPie()
    {
        JsonElement result = Parse(_db.GetMarketShare(category: "Grocery"));
        result.TryGetProperty("national_share", out JsonElement national).Should().BeTrue();
        JsonElement entries = national.GetProperty("entries");
        entries.GetArrayLength().Should().BeGreaterThanOrEqualTo(2,
            "the #21 pie prompt requires >=2 grocery brands in the national aggregate");
    }

    [Fact]
    public void GetMarketShare_RegionScoped_DoesNotEmitNationalAggregate()
    {
        JsonElement result = Parse(_db.GetMarketShare(category: "Spirits", region: "Northeast"));

        if (result.TryGetProperty("national_share", out JsonElement national))
        {
            national.ValueKind.Should().Be(JsonValueKind.Null,
                "national_share must be null when the caller scoped to a specific region");
        }
    }
}

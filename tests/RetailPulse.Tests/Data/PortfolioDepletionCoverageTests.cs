using System.Text.Json;
using FluentAssertions;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;
using RetailPulse.Tests.TestInfrastructure;
using Xunit;

namespace RetailPulse.Tests.Data;

/// <summary>
/// Regression coverage for issue #74 — the horizontal-bar "rank all brands by
/// depletion growth rate" prompt drives a single call to
/// <see cref="RetailPulseDb.GetPortfolioDepletionStats"/> with region="National".
/// Before the fix, that call returned one <c>{ error }</c> row per brand because
/// the seeder never emits a "National" row and <c>GetDepletionStats</c>'s
/// <c>LIKE %National%</c> filter never matched. This test pins the aggregate
/// contract: every tenant brand must come back with a finite, parseable
/// <c>metrics.depletions_yoy</c> percent — no error rows, no missing brands.
/// </summary>
public class PortfolioDepletionCoverageTests : IDisposable
{
    private readonly string _dbPath;
    private readonly RetailPulseDb _db;
    private readonly TenantConfiguration _tenant;

    public PortfolioDepletionCoverageTests()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");

        _dbPath = SqliteTestCleanup.NewDbPath("rp_portfolio_coverage");
        var tenantProvider = new FileTenantProvider(tenantConfigPath);
        _tenant = tenantProvider.GetTenant();
        _db = new RetailPulseDb(tenantProvider, _dbPath, tenantConfigPath);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        SqliteTestCleanup.ReleaseAndDelete(_dbPath);
    }

    [Fact]
    public void GetPortfolioDepletionStats_National_ReturnsFiniteGrowthForEveryTenantBrand()
    {
        object result = _db.GetPortfolioDepletionStats("National", "YTD");
        AssertEveryBrandHasFiniteGrowth(result, "National");
    }

    [Theory]
    [InlineData("Northeast")]
    [InlineData("Southeast")]
    [InlineData("Midwest")]
    [InlineData("Southwest")]
    [InlineData("West Coast")]
    [InlineData("Pacific Northwest")]
    public void GetPortfolioDepletionStats_PerRegion_ReturnsFiniteGrowthForEveryBrand(string region)
    {
        object result = _db.GetPortfolioDepletionStats(region, "YTD");
        AssertEveryBrandHasFiniteGrowth(result, region);
    }

    [Fact]
    public void GetPortfolioDepletionStats_BlankRegion_DefaultsToNationalAggregate()
    {
        object result = _db.GetPortfolioDepletionStats("", "YTD");
        AssertEveryBrandHasFiniteGrowth(result, "National");
    }

    private void AssertEveryBrandHasFiniteGrowth(object result, string expectedRegion)
    {
        // Serialize once and use the same reflection-free path other data tests use.
        string json = JsonSerializer.Serialize(result);
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.TryGetProperty("brands", out JsonElement brands)
            .Should().BeTrue("the aggregate must expose a brands[] array");
        brands.ValueKind.Should().Be(JsonValueKind.Array);

        int expectedCount = _tenant.Brands.Count;
        brands.GetArrayLength().Should().Be(expectedCount,
            $"one row per tenant brand ({expectedCount}) is expected for region '{expectedRegion}'");

        var seenBrands = new List<string>();
        foreach (JsonElement brand in brands.EnumerateArray())
        {
            brand.TryGetProperty("error", out _).Should().BeFalse(
                $"no brand row may be an error shape for region '{expectedRegion}': {brand}");

            brand.TryGetProperty("brand", out JsonElement brandName).Should().BeTrue();
            seenBrands.Add(brandName.GetString()!);

            brand.TryGetProperty("metrics", out JsonElement metrics)
                .Should().BeTrue($"brand row must carry metrics ({brandName.GetString()})");
            metrics.TryGetProperty("depletions_yoy", out JsonElement yoy)
                .Should().BeTrue($"metrics must carry depletions_yoy ({brandName.GetString()})");
            string yoyString = yoy.GetString() ?? "";
            TryParseSignedPercent(yoyString, out double value)
                .Should().BeTrue($"depletions_yoy for '{brandName.GetString()}' must be a parseable signed percent (got '{yoyString}')");
            double.IsFinite(value).Should().BeTrue();
        }

        // Every tenant brand appears — no silent drops.
        foreach (BrandConfig expected in _tenant.Brands)
        {
            seenBrands.Should().Contain(expected.Name,
                $"tenant brand '{expected.Name}' must appear in aggregate for region '{expectedRegion}'");
        }
    }

    private static bool TryParseSignedPercent(string text, out double value)
    {
        value = double.NaN;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string trimmed = text.Trim().TrimEnd('%').Trim();
        if (trimmed.StartsWith('+')) trimmed = trimmed[1..];
        return double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}

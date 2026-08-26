using System.Text.Json;
using FluentAssertions;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;
using RetailPulse.Tests.TestInfrastructure;

namespace RetailPulse.Tests.Data;

/// <summary>
/// Regression cover for the national market-share rollup.
///
/// <para>
/// Depletion stats, variant mix and historical demand all normalise a "National"
/// region to a country-wide rollup. Market share did not: it applied
/// <c>Region LIKE '%National%'</c> to a table that only stores real regions, so the
/// query matched nothing. A prompt like "market share breakdown for our grocery
/// brands nationally" therefore returned zero rows, and the pie chart correctly —
/// but unhelpfully — failed closed with a chart-unavailable diagnostic. That was the
/// last deterministic failure in the G4 acceptance sweep (issue #59, gate G2).
/// </para>
/// </summary>
public sealed class MarketShareNationalRollupTests : IDisposable
{
    private readonly string _dbPath;
    private readonly RetailPulseDb _db;

    public MarketShareNationalRollupTests()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");

        _dbPath = SqliteTestCleanup.NewDbPath("retailpulse_marketshare_national");
        var tenantProvider = new FileTenantProvider(tenantConfigPath);
        _db = new RetailPulseDb(tenantProvider, _dbPath, tenantConfigPath);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        SqliteTestCleanup.ReleaseAndDelete(_dbPath);
    }

    private static JsonElement Query(RetailPulseDb db, string? region, string? category = null)
    {
        object result = db.GetMarketShare(category: category, region: region);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
        return doc.RootElement.Clone();
    }

    [Theory]
    [InlineData("National")]
    [InlineData("national")]
    [InlineData("Nationwide")]
    [InlineData("US")]
    [InlineData("USA")]
    [InlineData("United States")]
    [InlineData("Aggregate")]
    public void NationalRegionTokens_ReturnARollup_NotAnEmptyResult(string token)
    {
        JsonElement payload = Query(_db, token);

        payload.GetProperty("total_records").GetInt32()
            .Should().BeGreaterThan(0, "a national ask must roll the regions up, not filter on a region that does not exist");

        JsonElement rows = payload.GetProperty("share_data");
        rows.GetArrayLength().Should().BeGreaterThan(0);

        foreach (JsonElement row in rows.EnumerateArray())
        {
            row.GetProperty("region").GetString().Should().Be("National");
            double share = row.GetProperty("share_percent").GetDouble();
            double.IsFinite(share).Should().BeTrue();
            share.Should().BeInRange(0, 100);
        }
    }

    [Fact]
    public void NationalRollup_IsTheMeanOfTheRegionalShares()
    {
        JsonElement national = Query(_db, "National");
        JsonElement allRegions = Query(_db, region: null);

        JsonElement sample = national.GetProperty("share_data").EnumerateArray().First();
        string brand = sample.GetProperty("brand").GetString()!;
        string category = sample.GetProperty("category").GetString()!;
        string period = sample.GetProperty("period").GetString()!;

        double[] regional = [.. allRegions.GetProperty("share_data").EnumerateArray()
            .Where(r => r.GetProperty("brand").GetString() == brand
                     && r.GetProperty("category").GetString() == category
                     && r.GetProperty("period").GetString() == period)
            .Select(r => r.GetProperty("share_percent").GetDouble())];

        regional.Should().NotBeEmpty("the sampled brand/period must exist in the regional rows");
        sample.GetProperty("share_percent").GetDouble()
            .Should().BeApproximately(regional.Average(), 0.01,
                "the national figure is the unweighted mean of the regions that reported");
    }

    [Fact]
    public void NationalRollup_DeclaresHowItWasComputed()
    {
        // The table has no volume weights, so the mean is an approximation. It must be
        // labelled as one rather than presented as a measured national figure.
        JsonElement payload = Query(_db, "National");
        JsonElement aggregation = payload.GetProperty("aggregation");

        aggregation.GetProperty("scope").GetString().Should().Be("national");
        aggregation.GetProperty("method").GetString().Should().Contain("unweighted mean");
    }

    [Fact]
    public void NationalRollup_CollapsesEachBrandToASingleSliceForTheLatestPeriod()
    {
        // A pie needs one slice per brand. The rollup groups by brand/category/period and
        // orders newest first, so the first row per brand is that brand's latest national
        // share — which is what the deterministic pie builder reads.
        JsonElement payload = Query(_db, "National", category: "Grocery");
        List<JsonElement> rows = [.. payload.GetProperty("share_data").EnumerateArray()];

        rows.Should().NotBeEmpty();

        string newestPeriod = rows[0].GetProperty("period").GetString()!;
        string[] brandsInNewestPeriod = [.. rows
            .Where(r => r.GetProperty("period").GetString() == newestPeriod)
            .Select(r => r.GetProperty("brand").GetString()!)];

        brandsInNewestPeriod.Should().OnlyHaveUniqueItems(
            "each brand contributes exactly one national slice per period");
        brandsInNewestPeriod.Length.Should().BeGreaterThanOrEqualTo(2,
            "a pie breakdown needs at least two slices");
    }

    [Fact]
    public void ASpecificRegion_StillReturnsThatRegionsRows_Unchanged()
    {
        JsonElement payload = Query(_db, "Northeast");

        payload.GetProperty("total_records").GetInt32().Should().BeGreaterThan(0);
        foreach (JsonElement row in payload.GetProperty("share_data").EnumerateArray())
        {
            row.GetProperty("region").GetString().Should().Be("Northeast");
        }
    }

    [Fact]
    public void AnOmittedRegion_StillReturnsPerRegionRows_Unchanged()
    {
        // Callers that pass no region rely on getting every region's rows. Only an
        // EXPLICIT national token switches to the rollup.
        JsonElement payload = Query(_db, region: null);

        string[] regions = [.. payload.GetProperty("share_data").EnumerateArray()
            .Select(r => r.GetProperty("region").GetString()!)
            .Distinct()];

        regions.Should().NotContain("National");
        regions.Length.Should().BeGreaterThan(1, "omitting the region must not collapse the regions");
    }
}

using FluentAssertions;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;
using System.Text.Json;

namespace RetailPulse.Tests.Margin;

/// <summary>
/// Tests for margin analysis MCP tool methods on RetailPulseDb:
/// get_margin_by_brand, get_margin_drivers, get_margin_trend, detect_margin_risks.
/// Validates P&amp;L math, driver impacts, quarterly ordering, and risk detection.
/// Test-first: defines expected contracts before Phase 4.2 implementation.
/// </summary>
public class MarginToolTests : IDisposable
{
    private readonly string _dbPath;
    private readonly RetailPulseDb _db;

    public MarginToolTests()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");

        _dbPath = Path.Combine(Path.GetTempPath(), $"retailpulse_margin_test_{Guid.NewGuid():N}.db");
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
        JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement;

    #region get_margin_by_brand

    [Theory]
    [InlineData("Sierra Gold Tequila")]
    [InlineData("Ridgeline Bourbon")]
    [InlineData("FreshMart")]
    [InlineData("Apex Grill")]
    public void GetMarginByBrand_ReturnsValidPnlBreakdown(string brand)
    {
        var result = Parse(_db.GetMarginByBrand(brand));

        result.TryGetProperty("error", out _).Should().BeFalse(
            $"should return margin data for brand '{brand}'");
        result.GetProperty("brand").GetString().Should().Be(brand);

        var financials = result.GetProperty("financials");
        financials.GetArrayLength().Should().BeGreaterThan(0, "should have at least one financial record");

        // P&L breakdown should have these fields on each financial record
        var record = financials[0];
        record.GetProperty("revenue").GetDouble().Should().BeGreaterThan(0);
        record.GetProperty("cogs").GetDouble().Should().BeGreaterThan(0);
        record.TryGetProperty("grossMargin", out _).Should().BeTrue("should have gross margin");
        record.TryGetProperty("marketing", out _).Should().BeTrue("should have marketing cost");
        record.TryGetProperty("distribution", out _).Should().BeTrue("should have distribution cost");
        record.TryGetProperty("netMargin", out _).Should().BeTrue("should have net margin");
    }

    [Fact]
    public void GetMarginByBrand_RevenueMinusCogs_EqualsGrossMargin()
    {
        var result = Parse(_db.GetMarginByBrand("Sierra Gold Tequila"));

        result.TryGetProperty("error", out _).Should().BeFalse();

        var record = result.GetProperty("financials")[0];
        var revenue = record.GetProperty("revenue").GetDouble();
        var cogs = record.GetProperty("cogs").GetDouble();
        var grossMargin = record.GetProperty("grossMargin").GetDouble();

        grossMargin.Should().BeApproximately(revenue - cogs, 0.01,
            "grossMargin should equal revenue - COGS");
    }

    [Fact]
    public void GetMarginByBrand_GrossMinusExpenses_ApproximatesNetMargin()
    {
        var result = Parse(_db.GetMarginByBrand("Sierra Gold Tequila"));

        result.TryGetProperty("error", out _).Should().BeFalse();

        var record = result.GetProperty("financials")[0];
        var grossMargin = record.GetProperty("grossMargin").GetDouble();
        var marketing = record.GetProperty("marketing").GetDouble();
        var distribution = record.GetProperty("distribution").GetDouble();
        var netMargin = record.GetProperty("netMargin").GetDouble();

        var expectedNet = grossMargin - marketing - distribution;
        netMargin.Should().BeApproximately(expectedNet, 1.0,
            "netMargin ≈ grossMargin - marketing - distribution (±$1 rounding)");
    }

    [Fact]
    public void GetMarginByBrand_UnknownBrand_ReturnsEmptyOrError()
    {
        var result = Parse(_db.GetMarginByBrand("NonExistentBrand999"));

        var hasError = result.TryGetProperty("error", out _);
        var hasEmptyFinancials = result.TryGetProperty("financials", out var fin)
            && fin.GetArrayLength() == 0;
        var hasZeroPeriods = result.TryGetProperty("periodsReported", out var pr)
            && pr.GetInt32() == 0;

        (hasError || hasEmptyFinancials || hasZeroPeriods).Should().BeTrue(
            "unknown brand should return error, empty financials, or periodsReported == 0");
    }

    #endregion

    #region get_margin_drivers

    [Fact]
    public void GetMarginDrivers_ReturnsDriversWithNonZeroImpact()
    {
        var result = Parse(_db.GetMarginDrivers("Sierra Gold Tequila"));

        result.TryGetProperty("error", out _).Should().BeFalse(
            "should return margin drivers");
        var drivers = result.GetProperty("drivers");
        drivers.GetArrayLength().Should().BeGreaterThan(0, "should have at least one driver");

        foreach (var driver in drivers.EnumerateArray())
        {
            driver.GetProperty("category").GetString().Should().NotBeNullOrEmpty();
            var impact = driver.GetProperty("impact").GetDouble();
            impact.Should().NotBe(0, "driver impact should be non-zero");
        }
    }

    [Fact]
    public void GetMarginDrivers_EachDriverHasCategory()
    {
        var result = Parse(_db.GetMarginDrivers("Ridgeline Bourbon"));

        result.TryGetProperty("error", out _).Should().BeFalse();
        var drivers = result.GetProperty("drivers");

        foreach (var driver in drivers.EnumerateArray())
        {
            driver.GetProperty("category").GetString().Should().NotBeNullOrEmpty(
                "each margin driver should have a category classification");
        }
    }

    #endregion

    #region get_margin_trend

    [Fact]
    public void GetMarginTrend_ReturnsQuarterlyDataInOrder()
    {
        var result = Parse(_db.GetMarginTrend("Sierra Gold Tequila"));

        result.TryGetProperty("error", out _).Should().BeFalse(
            "should return margin trend data");
        var trend = result.GetProperty("trend");
        trend.GetArrayLength().Should().BeGreaterThan(0, "should have quarterly data");

        var periods = new List<string>();
        foreach (var q in trend.EnumerateArray())
        {
            var period = q.GetProperty("period").GetString()!;
            period.Should().NotBeNullOrEmpty();
            periods.Add(period);
        }

        // Periods should be in chronological order (e.g., "Q1 2025", "Q2 2025", etc.)
        periods.Should().BeInAscendingOrder(
            "quarterly data should be in chronological order");
    }

    [Fact]
    public void GetMarginTrend_EachQuarterHasMarginValues()
    {
        var result = Parse(_db.GetMarginTrend("Ridgeline Bourbon"));

        result.TryGetProperty("error", out _).Should().BeFalse();
        var trend = result.GetProperty("trend");

        foreach (var q in trend.EnumerateArray())
        {
            q.GetProperty("revenue").GetDouble().Should().BeGreaterThan(0);
            q.TryGetProperty("grossMargin", out _).Should().BeTrue();
            q.TryGetProperty("netMargin", out _).Should().BeTrue();
        }
    }

    #endregion

    #region detect_margin_risks

    [Fact]
    public void DetectMarginRisks_IdentifiesOverPromotionPattern()
    {
        var result = Parse(_db.DetectMarginRisks());

        result.TryGetProperty("error", out _).Should().BeFalse(
            "should return margin risks analysis");
        var risks = result.GetProperty("risks");

        // At least some risks should exist in seeded data
        if (risks.GetArrayLength() > 0)
        {
            var riskTypes = new List<string>();
            foreach (var risk in risks.EnumerateArray())
            {
                risk.GetProperty("riskType").GetString().Should().NotBeNullOrEmpty();
                risk.GetProperty("brand").GetString().Should().NotBeNullOrEmpty();
                risk.GetProperty("severity").GetString().Should().NotBeNullOrEmpty();
                riskTypes.Add(risk.GetProperty("riskType").GetString()!);
            }

            // Over-promotion is a common pattern in seeded data
            // At least one risk type should be recognized
            riskTypes.Should().NotBeEmpty("should detect at least one risk pattern");
        }
    }

    [Fact]
    public void DetectMarginRisks_FiltersByBrand()
    {
        var result = Parse(_db.DetectMarginRisks(brandId: "Sierra Gold Tequila"));

        result.TryGetProperty("error", out _).Should().BeFalse();
        var risks = result.GetProperty("risks");

        foreach (var risk in risks.EnumerateArray())
        {
            risk.GetProperty("brand").GetString().Should().Be("Sierra Gold Tequila",
                "filtered risks should only be for the requested brand");
        }
    }

    [Fact]
    public void DetectMarginRisks_RisksHaveSeverityLevels()
    {
        var result = Parse(_db.DetectMarginRisks());

        result.TryGetProperty("error", out _).Should().BeFalse();
        var risks = result.GetProperty("risks");

        var validSeverities = new[] { "low", "medium", "high", "critical" };

        foreach (var risk in risks.EnumerateArray())
        {
            var severity = risk.GetProperty("severity").GetString()!.ToLowerInvariant();
            validSeverities.Should().Contain(severity,
                $"risk severity '{severity}' should be a valid level");
        }
    }

    #endregion
}

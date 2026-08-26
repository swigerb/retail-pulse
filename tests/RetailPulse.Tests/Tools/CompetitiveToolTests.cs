using System.Text.Json;
using FluentAssertions;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;
using RetailPulse.Tests.TestInfrastructure;

namespace RetailPulse.Tests.Tools;

/// <summary>
/// Tests for the competitive intelligence data layer methods on RetailPulseDb:
/// GetCompetitorPricing, GetMarketShare, DetectCompetitiveThreats, GetCompetitiveLandscape.
/// Uses a real SQLite DB with seeded data from tenant.yaml.
/// </summary>
public class CompetitiveToolTests : IDisposable
{
    private readonly string _dbPath;
    private readonly RetailPulseDb _db;

    public CompetitiveToolTests()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");

        _dbPath = SqliteTestCleanup.NewDbPath("retailpulse_comp_test");
        var tenantProvider = new FileTenantProvider(tenantConfigPath);
        _db = new RetailPulseDb(tenantProvider, _dbPath, tenantConfigPath);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        SqliteTestCleanup.ReleaseAndDelete(_dbPath);
    }

    private static JsonElement Parse(object obj) =>
        JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement;

    #region GetCompetitorPricing

    [Theory]
    [InlineData("Spirits", "Northeast")]
    [InlineData("Grocery", "Southeast")]
    [InlineData("Quick-Serve Restaurant", "Midwest")]
    public void GetCompetitorPricing_ValidCategoryRegion_ReturnsData(string category, string region)
    {
        JsonElement result = Parse(_db.GetCompetitorPricing(category: category, region: region));

        result.GetProperty("total_records").GetInt32().Should().BeGreaterThan(0,
            $"should return pricing data for category '{category}' in region '{region}'");
    }

    [Fact]
    public void GetCompetitorPricing_FiltersByCategory()
    {
        JsonElement result = Parse(_db.GetCompetitorPricing(category: "Spirits"));

        result.GetProperty("total_records").GetInt32().Should().BeGreaterThan(0);
        JsonElement pricing = result.GetProperty("pricing");

        foreach (JsonElement item in pricing.EnumerateArray())
        {
            item.GetProperty("category").GetString()!.ToLower(System.Globalization.CultureInfo.CurrentCulture).Should().Contain("spirits");
        }
    }

    [Fact]
    public void GetCompetitorPricing_FiltersByRegion()
    {
        JsonElement result = Parse(_db.GetCompetitorPricing(category: "Spirits", region: "Northeast"));

        result.GetProperty("total_records").GetInt32().Should().BeGreaterThan(0);
        JsonElement pricing = result.GetProperty("pricing");

        foreach (JsonElement item in pricing.EnumerateArray())
        {
            item.GetProperty("region").GetString()!.ToLower(System.Globalization.CultureInfo.CurrentCulture).Should().Contain("northeast");
        }
    }

    [Fact]
    public void GetCompetitorPricing_AllPricesPositive()
    {
        JsonElement result = Parse(_db.GetCompetitorPricing(category: "Spirits"));

        foreach (JsonElement item in result.GetProperty("pricing").EnumerateArray())
        {
            item.GetProperty("price").GetDouble().Should().BeGreaterThan(0,
                "competitor prices must be positive values");
        }
    }

    [Fact]
    public void GetCompetitorPricing_UnknownCategory_ReturnsEmptyNotCrash()
    {
        JsonElement result = Parse(_db.GetCompetitorPricing(category: "AlienTechnology", region: "Northeast"));

        // Should return zero records, not crash
        result.GetProperty("total_records").GetInt32().Should().Be(0);
    }

    [Fact]
    public void GetCompetitorPricing_IncludesCompetitorNames()
    {
        JsonElement result = Parse(_db.GetCompetitorPricing(category: "Spirits"));

        foreach (JsonElement item in result.GetProperty("pricing").EnumerateArray())
        {
            item.GetProperty("competitor").GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void GetCompetitorPricing_IdentifiesPriceDropThreats()
    {
        // The method identifies price drops >10% as threats
        JsonElement result = Parse(_db.GetCompetitorPricing(category: "Spirits"));

        result.TryGetProperty("price_drop_threats", out JsonElement threats).Should().BeTrue();
        threats.GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void GetCompetitorPricing_FiltersByBrand()
    {
        JsonElement result = Parse(_db.GetCompetitorPricing(brand: "Sierra Gold Tequila"));

        foreach (JsonElement item in result.GetProperty("pricing").EnumerateArray())
        {
            item.GetProperty("brand").GetString()!.ToLower(System.Globalization.CultureInfo.CurrentCulture).Should().Contain("sierra gold tequila");
        }
    }

    #endregion

    #region GetMarketShare

    [Theory]
    [InlineData("Spirits", "Northeast")]
    [InlineData("Grocery", "Southeast")]
    public void GetMarketShare_ValidCategoryRegion_ReturnsData(string category, string region)
    {
        JsonElement result = Parse(_db.GetMarketShare(category: category, region: region));

        result.GetProperty("total_records").GetInt32().Should().BeGreaterThan(0,
            $"should return market share data for category '{category}' in region '{region}'");
    }

    [Fact]
    public void GetMarketShare_SharesInValidRange()
    {
        JsonElement result = Parse(_db.GetMarketShare(category: "Spirits", region: "Northeast"));

        foreach (JsonElement item in result.GetProperty("share_data").EnumerateArray())
        {
            double share = item.GetProperty("share_percent").GetDouble();
            share.Should().BeGreaterThanOrEqualTo(0, "market share cannot be negative");
            share.Should().BeLessThanOrEqualTo(100, "market share cannot exceed 100%");
        }
    }

    [Fact]
    public void GetMarketShare_IncludesBrandAndCategory()
    {
        JsonElement result = Parse(_db.GetMarketShare(category: "Spirits", region: "Northeast"));

        foreach (JsonElement item in result.GetProperty("share_data").EnumerateArray())
        {
            item.GetProperty("brand").GetString().Should().NotBeNullOrWhiteSpace();
            item.GetProperty("category").GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void GetMarketShare_HandlesMissingPeriod_Gracefully()
    {
        JsonElement result = Parse(_db.GetMarketShare(category: "Spirits", region: "Northeast", period: "2099-Q4"));

        // Should not throw — returns empty data for unknown period
        result.GetProperty("total_records").GetInt32().Should().Be(0);
    }

    [Fact]
    public void GetMarketShare_IdentifiesSignificantShareLosses()
    {
        JsonElement result = Parse(_db.GetMarketShare(category: "Spirits"));

        result.TryGetProperty("significant_share_losses", out JsonElement losses).Should().BeTrue();
        losses.GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }

    #endregion

    #region DetectCompetitiveThreats

    [Fact]
    public void DetectCompetitiveThreats_ReturnsThreats()
    {
        JsonElement result = Parse(_db.DetectCompetitiveThreats());

        result.GetProperty("total_threats").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        JsonElement threats = result.GetProperty("threats");

        foreach (JsonElement threat in threats.EnumerateArray())
        {
            threat.GetProperty("type").GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void DetectCompetitiveThreats_CategorizesBySeverity()
    {
        JsonElement result = Parse(_db.DetectCompetitiveThreats());

        JsonElement threats = result.GetProperty("threats");
        foreach (JsonElement threat in threats.EnumerateArray())
        {
            string? severity = threat.GetProperty("severity").GetString();
            severity.Should().BeOneOf("high", "medium",
                "threat severity should be categorized as high or medium");
        }
    }

    [Fact]
    public void DetectCompetitiveThreats_IncludesRecommendation()
    {
        JsonElement result = Parse(_db.DetectCompetitiveThreats());

        JsonElement threats = result.GetProperty("threats");
        foreach (JsonElement threat in threats.EnumerateArray())
        {
            string? recommendation = threat.GetProperty("recommendation").GetString();
            recommendation.Should().BeOneOf("MATCH", "DIFFERENTIATE", "IGNORE", "PREEMPT",
                "each threat should include a defensive recommendation");
        }
    }

    [Fact]
    public void DetectCompetitiveThreats_FiltersByBrand()
    {
        JsonElement result = Parse(_db.DetectCompetitiveThreats(brand: "Sierra Gold Tequila"));

        JsonElement threats = result.GetProperty("threats");
        foreach (JsonElement threat in threats.EnumerateArray())
        {
            // Price_drop threats have brand; activity threats may have null brand
            if (threat.GetProperty("type").GetString() == "price_drop")
            {
                string? brand = threat.GetProperty("brand").GetString();
                brand?.ToLower(System.Globalization.CultureInfo.CurrentCulture).Should().Contain("sierra gold tequila");
            }
        }
    }

    [Fact]
    public void DetectCompetitiveThreats_FiltersByCategory()
    {
        JsonElement result = Parse(_db.DetectCompetitiveThreats(category: "Spirits"));

        JsonElement threats = result.GetProperty("threats");
        foreach (JsonElement threat in threats.EnumerateArray())
        {
            threat.GetProperty("category").GetString()!.ToLower(System.Globalization.CultureInfo.CurrentCulture).Should().Contain("spirit");
        }
    }

    [Fact]
    public void DetectCompetitiveThreats_SeverityCounts_Match()
    {
        JsonElement result = Parse(_db.DetectCompetitiveThreats());

        int high = result.GetProperty("high_severity").GetInt32();
        int medium = result.GetProperty("medium_severity").GetInt32();
        int total = result.GetProperty("total_threats").GetInt32();

        (high + medium).Should().Be(total,
            "high + medium severity counts should equal total threats");
    }

    #endregion

    #region GetMarketShare — 6 Quarters of Data

    [Fact]
    public void GetMarketShare_Returns6QuartersOfData()
    {
        // The DB seeds exactly 6 quarters: 2025-Q1 through 2026-Q2
        JsonElement result = Parse(_db.GetMarketShare(category: "Spirits", region: "Northeast"));

        JsonElement shareData = result.GetProperty("share_data");
        shareData.GetArrayLength().Should().BeGreaterThan(0,
            "should return market share data");

        // Collect distinct periods across all records
        var periods = shareData.EnumerateArray()
            .Select(r => r.GetProperty("period").GetString())
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        periods.Should().HaveCount(6,
            "market share data should span exactly 6 quarters (2025-Q1 through 2026-Q2)");

        periods.Should().Contain("2025-Q1");
        periods.Should().Contain("2026-Q2");
    }

    [Fact]
    public void GetMarketShare_PeriodsAreValidQuarterFormat()
    {
        JsonElement result = Parse(_db.GetMarketShare(category: "Spirits"));

        foreach (JsonElement item in result.GetProperty("share_data").EnumerateArray())
        {
            string? period = item.GetProperty("period").GetString();
            period.Should().MatchRegex(@"^\d{4}-Q[1-4]$",
                $"period '{period}' should be in yyyy-Qn format");
        }
    }

    #endregion

    #region GetCompetitiveLandscape

    [Fact]
    public void GetCompetitiveLandscape_ReturnsFullOverview()
    {
        JsonElement result = Parse(_db.GetCompetitiveLandscape("Spirits", "Northeast"));

        result.TryGetProperty("error", out _).Should().BeFalse();

        result.GetProperty("category").GetString().Should().Be("Spirits");
        result.GetProperty("region").GetString().Should().Be("Northeast");
        result.GetProperty("total_players").GetInt32().Should().BeGreaterThan(0);
        result.GetProperty("recent_activities").GetArrayLength().Should().BeGreaterThanOrEqualTo(0);
        result.GetProperty("pricing_moves").GetArrayLength().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void GetCompetitiveLandscape_MissingCategory_ReturnsError()
    {
        JsonElement result = Parse(_db.GetCompetitiveLandscape("", "Northeast"));

        result.TryGetProperty("error", out _).Should().BeTrue(
            "empty category should return an error");
    }

    [Fact]
    public void GetCompetitiveLandscape_MissingRegion_ReturnsError()
    {
        JsonElement result = Parse(_db.GetCompetitiveLandscape("Spirits", ""));

        result.TryGetProperty("error", out _).Should().BeTrue(
            "empty region should return an error");
    }

    [Fact]
    public void GetCompetitiveLandscape_UnknownCategory_ReturnsEmptyPlayers()
    {
        JsonElement result = Parse(_db.GetCompetitiveLandscape("AlienTechnology", "Northeast"));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("total_players").GetInt32().Should().Be(0);
    }

    [Fact]
    public void GetCompetitiveLandscape_IncludesOurBrandsAndCompetitors()
    {
        JsonElement result = Parse(_db.GetCompetitiveLandscape("Spirits", "Northeast"));

        result.TryGetProperty("our_brands", out _).Should().BeTrue(
            "landscape should identify our brands");
        result.TryGetProperty("competitors", out _).Should().BeTrue(
            "landscape should identify competitor brands");
    }

    #endregion
}

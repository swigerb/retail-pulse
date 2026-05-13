using FluentAssertions;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;
using System.Text.Json;

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
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");

        _dbPath = Path.Combine(Path.GetTempPath(), $"retailpulse_comp_test_{Guid.NewGuid():N}.db");
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

    #region GetCompetitorPricing

    [Theory]
    [InlineData("Spirits", "Northeast")]
    [InlineData("Grocery", "Southeast")]
    [InlineData("Quick-Serve Restaurant", "Midwest")]
    public void GetCompetitorPricing_ValidCategoryRegion_ReturnsData(string category, string region)
    {
        var result = Parse(_db.GetCompetitorPricing(category: category, region: region));

        result.GetProperty("total_records").GetInt32().Should().BeGreaterThan(0,
            $"should return pricing data for category '{category}' in region '{region}'");
    }

    [Fact]
    public void GetCompetitorPricing_FiltersByCategory()
    {
        var result = Parse(_db.GetCompetitorPricing(category: "Spirits"));

        result.GetProperty("total_records").GetInt32().Should().BeGreaterThan(0);
        var pricing = result.GetProperty("pricing");

        foreach (var item in pricing.EnumerateArray())
        {
            item.GetProperty("category").GetString()!.ToLower().Should().Contain("spirits");
        }
    }

    [Fact]
    public void GetCompetitorPricing_FiltersByRegion()
    {
        var result = Parse(_db.GetCompetitorPricing(category: "Spirits", region: "Northeast"));

        result.GetProperty("total_records").GetInt32().Should().BeGreaterThan(0);
        var pricing = result.GetProperty("pricing");

        foreach (var item in pricing.EnumerateArray())
        {
            item.GetProperty("region").GetString()!.ToLower().Should().Contain("northeast");
        }
    }

    [Fact]
    public void GetCompetitorPricing_AllPricesPositive()
    {
        var result = Parse(_db.GetCompetitorPricing(category: "Spirits"));

        foreach (var item in result.GetProperty("pricing").EnumerateArray())
        {
            item.GetProperty("price").GetDouble().Should().BeGreaterThan(0,
                "competitor prices must be positive values");
        }
    }

    [Fact]
    public void GetCompetitorPricing_UnknownCategory_ReturnsEmptyNotCrash()
    {
        var result = Parse(_db.GetCompetitorPricing(category: "AlienTechnology", region: "Northeast"));

        // Should return zero records, not crash
        result.GetProperty("total_records").GetInt32().Should().Be(0);
    }

    [Fact]
    public void GetCompetitorPricing_IncludesCompetitorNames()
    {
        var result = Parse(_db.GetCompetitorPricing(category: "Spirits"));

        foreach (var item in result.GetProperty("pricing").EnumerateArray())
        {
            item.GetProperty("competitor").GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void GetCompetitorPricing_IdentifiesPriceDropThreats()
    {
        // The method identifies price drops >10% as threats
        var result = Parse(_db.GetCompetitorPricing(category: "Spirits"));

        result.TryGetProperty("price_drop_threats", out var threats).Should().BeTrue();
        threats.GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void GetCompetitorPricing_FiltersByBrand()
    {
        var result = Parse(_db.GetCompetitorPricing(brand: "Sierra Gold Tequila"));

        foreach (var item in result.GetProperty("pricing").EnumerateArray())
        {
            item.GetProperty("brand").GetString()!.ToLower().Should().Contain("sierra gold tequila");
        }
    }

    #endregion

    #region GetMarketShare

    [Theory]
    [InlineData("Spirits", "Northeast")]
    [InlineData("Grocery", "Southeast")]
    public void GetMarketShare_ValidCategoryRegion_ReturnsData(string category, string region)
    {
        var result = Parse(_db.GetMarketShare(category: category, region: region));

        result.GetProperty("total_records").GetInt32().Should().BeGreaterThan(0,
            $"should return market share data for category '{category}' in region '{region}'");
    }

    [Fact]
    public void GetMarketShare_SharesInValidRange()
    {
        var result = Parse(_db.GetMarketShare(category: "Spirits", region: "Northeast"));

        foreach (var item in result.GetProperty("share_data").EnumerateArray())
        {
            var share = item.GetProperty("share_percent").GetDouble();
            share.Should().BeGreaterThanOrEqualTo(0, "market share cannot be negative");
            share.Should().BeLessThanOrEqualTo(100, "market share cannot exceed 100%");
        }
    }

    [Fact]
    public void GetMarketShare_IncludesBrandAndCategory()
    {
        var result = Parse(_db.GetMarketShare(category: "Spirits", region: "Northeast"));

        foreach (var item in result.GetProperty("share_data").EnumerateArray())
        {
            item.GetProperty("brand").GetString().Should().NotBeNullOrWhiteSpace();
            item.GetProperty("category").GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void GetMarketShare_HandlesMissingPeriod_Gracefully()
    {
        var result = Parse(_db.GetMarketShare(category: "Spirits", region: "Northeast", period: "2099-Q4"));

        // Should not throw — returns empty data for unknown period
        result.GetProperty("total_records").GetInt32().Should().Be(0);
    }

    [Fact]
    public void GetMarketShare_IdentifiesSignificantShareLosses()
    {
        var result = Parse(_db.GetMarketShare(category: "Spirits"));

        result.TryGetProperty("significant_share_losses", out var losses).Should().BeTrue();
        losses.GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }

    #endregion

    #region DetectCompetitiveThreats

    [Fact]
    public void DetectCompetitiveThreats_ReturnsThreats()
    {
        var result = Parse(_db.DetectCompetitiveThreats());

        result.GetProperty("total_threats").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        var threats = result.GetProperty("threats");

        foreach (var threat in threats.EnumerateArray())
        {
            threat.GetProperty("type").GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void DetectCompetitiveThreats_CategorizesBySeverity()
    {
        var result = Parse(_db.DetectCompetitiveThreats());

        var threats = result.GetProperty("threats");
        foreach (var threat in threats.EnumerateArray())
        {
            var severity = threat.GetProperty("severity").GetString();
            severity.Should().BeOneOf("high", "medium",
                "threat severity should be categorized as high or medium");
        }
    }

    [Fact]
    public void DetectCompetitiveThreats_IncludesRecommendation()
    {
        var result = Parse(_db.DetectCompetitiveThreats());

        var threats = result.GetProperty("threats");
        foreach (var threat in threats.EnumerateArray())
        {
            var recommendation = threat.GetProperty("recommendation").GetString();
            recommendation.Should().BeOneOf("MATCH", "DIFFERENTIATE", "IGNORE", "PREEMPT",
                "each threat should include a defensive recommendation");
        }
    }

    [Fact]
    public void DetectCompetitiveThreats_FiltersByBrand()
    {
        var result = Parse(_db.DetectCompetitiveThreats(brand: "Sierra Gold Tequila"));

        var threats = result.GetProperty("threats");
        foreach (var threat in threats.EnumerateArray())
        {
            // Price_drop threats have brand; activity threats may have null brand
            if (threat.GetProperty("type").GetString() == "price_drop")
            {
                var brand = threat.GetProperty("brand").GetString();
                if (brand != null)
                {
                    brand.ToLower().Should().Contain("sierra gold tequila");
                }
            }
        }
    }

    [Fact]
    public void DetectCompetitiveThreats_FiltersByCategory()
    {
        var result = Parse(_db.DetectCompetitiveThreats(category: "Spirits"));

        var threats = result.GetProperty("threats");
        foreach (var threat in threats.EnumerateArray())
        {
            threat.GetProperty("category").GetString()!.ToLower().Should().Contain("spirit");
        }
    }

    [Fact]
    public void DetectCompetitiveThreats_SeverityCounts_Match()
    {
        var result = Parse(_db.DetectCompetitiveThreats());

        var high = result.GetProperty("high_severity").GetInt32();
        var medium = result.GetProperty("medium_severity").GetInt32();
        var total = result.GetProperty("total_threats").GetInt32();

        (high + medium).Should().Be(total,
            "high + medium severity counts should equal total threats");
    }

    #endregion

    #region GetCompetitiveLandscape

    [Fact]
    public void GetCompetitiveLandscape_ReturnsFullOverview()
    {
        var result = Parse(_db.GetCompetitiveLandscape("Spirits", "Northeast"));

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
        var result = Parse(_db.GetCompetitiveLandscape("", "Northeast"));

        result.TryGetProperty("error", out _).Should().BeTrue(
            "empty category should return an error");
    }

    [Fact]
    public void GetCompetitiveLandscape_MissingRegion_ReturnsError()
    {
        var result = Parse(_db.GetCompetitiveLandscape("Spirits", ""));

        result.TryGetProperty("error", out _).Should().BeTrue(
            "empty region should return an error");
    }

    [Fact]
    public void GetCompetitiveLandscape_UnknownCategory_ReturnsEmptyPlayers()
    {
        var result = Parse(_db.GetCompetitiveLandscape("AlienTechnology", "Northeast"));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("total_players").GetInt32().Should().Be(0);
    }

    [Fact]
    public void GetCompetitiveLandscape_IncludesOurBrandsAndCompetitors()
    {
        var result = Parse(_db.GetCompetitiveLandscape("Spirits", "Northeast"));

        result.TryGetProperty("our_brands", out _).Should().BeTrue(
            "landscape should identify our brands");
        result.TryGetProperty("competitors", out _).Should().BeTrue(
            "landscape should identify competitor brands");
    }

    #endregion
}

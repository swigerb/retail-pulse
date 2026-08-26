using System.Text.Json;
using FluentAssertions;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;
using RetailPulse.Tests.TestInfrastructure;

namespace RetailPulse.Tests.Tools;

/// <summary>
/// Tests for supply chain data layer methods on RetailPulseDb:
/// GetInventoryLevels, GetSupplyDisruptions, GetFulfillmentRates, GetSupplyHealthSummary.
/// Uses a real SQLite DB with seeded data from tenant.yaml.
/// </summary>
public class SupplyToolTests : IDisposable
{
    private readonly string _dbPath;
    private readonly RetailPulseDb _db;

    public SupplyToolTests()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");

        _dbPath = SqliteTestCleanup.NewDbPath("retailpulse_supply_test");
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

    #region GetInventoryLevels

    [Theory]
    [InlineData("Sierra Gold Tequila", "Northeast")]
    [InlineData("Ridgeline Bourbon", "Southeast")]
    [InlineData("FreshMart", "Midwest")]
    public void GetInventoryLevels_ValidBrandRegion_ReturnsData(string brand, string region)
    {
        JsonElement result = Parse(_db.GetInventoryLevels(brand, region, null, null));

        result.GetProperty("total_items").GetInt32().Should().BeGreaterThan(0,
            $"should return inventory data for brand '{brand}' in region '{region}'");
    }

    [Fact]
    public void GetInventoryLevels_FiltersByBrand()
    {
        JsonElement result = Parse(_db.GetInventoryLevels("Sierra Gold Tequila", null, null, null));

        result.GetProperty("total_items").GetInt32().Should().BeGreaterThan(0);

        foreach (JsonElement item in result.GetProperty("items").EnumerateArray())
        {
            item.GetProperty("brand").GetString().Should().Contain("Sierra Gold Tequila");
        }
    }

    [Fact]
    public void GetInventoryLevels_FiltersByStatus()
    {
        JsonElement result = Parse(_db.GetInventoryLevels(null, null, null, "healthy"));

        result.GetProperty("total_items").GetInt32().Should().BeGreaterThan(0);

        foreach (JsonElement item in result.GetProperty("items").EnumerateArray())
        {
            item.GetProperty("status").GetString()!.ToLower(System.Globalization.CultureInfo.CurrentCulture).Should().Be("healthy");
        }
    }

    [Fact]
    public void GetInventoryLevels_UnknownBrand_ReturnsEmptyNotCrash()
    {
        JsonElement result = Parse(_db.GetInventoryLevels("NonExistentBrand99", null, null, null));

        result.GetProperty("total_items").GetInt32().Should().Be(0);
    }

    [Fact]
    public void GetInventoryLevels_AllDaysOfSupplyNonNegative()
    {
        JsonElement result = Parse(_db.GetInventoryLevels("Sierra Gold Tequila", null, null, null));

        foreach (JsonElement item in result.GetProperty("items").EnumerateArray())
        {
            item.GetProperty("days_of_supply").GetDouble().Should().BeGreaterThanOrEqualTo(0,
                "days of supply must be non-negative");
        }
    }

    #endregion

    #region GetSupplyDisruptions

    [Fact]
    public void GetSupplyDisruptions_ReturnsDisruptions()
    {
        JsonElement result = Parse(_db.GetSupplyDisruptions(null, null, null, false));

        result.GetProperty("total_disruptions").GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void GetSupplyDisruptions_FiltersBySeverity()
    {
        JsonElement result = Parse(_db.GetSupplyDisruptions(null, null, "high", false));

        foreach (JsonElement item in result.GetProperty("disruptions").EnumerateArray())
        {
            item.GetProperty("severity").GetString()!.ToLower(System.Globalization.CultureInfo.CurrentCulture).Should().Be("high");
        }
    }

    [Fact]
    public void GetSupplyDisruptions_FiltersByBrand()
    {
        JsonElement result = Parse(_db.GetSupplyDisruptions("Sierra Gold Tequila", null, null, false));

        foreach (JsonElement item in result.GetProperty("disruptions").EnumerateArray())
        {
            item.GetProperty("brand").GetString().Should().Contain("Sierra Gold Tequila");
        }
    }

    [Fact]
    public void GetSupplyDisruptions_ActiveOnly_FiltersCorrectly()
    {
        JsonElement result = Parse(_db.GetSupplyDisruptions(null, null, null, true));

        foreach (JsonElement item in result.GetProperty("disruptions").EnumerateArray())
        {
            item.GetProperty("is_active").GetBoolean().Should().BeTrue();
        }
    }

    [Fact]
    public void GetSupplyDisruptions_UnknownBrand_ReturnsEmptyNotCrash()
    {
        JsonElement result = Parse(_db.GetSupplyDisruptions("NonExistentBrand99", null, null, false));

        result.GetProperty("total_disruptions").GetInt32().Should().Be(0);
    }

    [Fact]
    public void GetSupplyDisruptions_HasValidSeverityValues()
    {
        JsonElement result = Parse(_db.GetSupplyDisruptions(null, null, null, false));
        string[] validSeverities = ["low", "medium", "high"];

        foreach (JsonElement item in result.GetProperty("disruptions").EnumerateArray())
        {
            string severity = item.GetProperty("severity").GetString()!.ToLower(System.Globalization.CultureInfo.CurrentCulture);
            severity.Should().BeOneOf(validSeverities,
                "disruption severity must be a valid level");
        }
    }

    #endregion

    #region GetFulfillmentRates

    [Theory]
    [InlineData("Sierra Gold Tequila")]
    [InlineData("FreshMart")]
    [InlineData("Apex Grill")]
    public void GetFulfillmentRates_ValidBrand_ReturnsData(string brand)
    {
        JsonElement result = Parse(_db.GetFulfillmentRates(brand, null, null, 1));

        result.GetProperty("total_periods").GetInt32().Should().BeGreaterThan(0,
            $"should return fulfillment data for brand '{brand}'");
    }

    [Fact]
    public void GetFulfillmentRates_RatesInValidRange()
    {
        JsonElement result = Parse(_db.GetFulfillmentRates("Sierra Gold Tequila", null, null, 1));

        foreach (JsonElement item in result.GetProperty("rates").EnumerateArray())
        {
            double rate = item.GetProperty("fill_rate").GetDouble();
            rate.Should().BeGreaterThanOrEqualTo(0, "fill rate cannot be negative");
            rate.Should().BeLessThanOrEqualTo(100, "fill rate cannot exceed 100%");
        }
    }

    [Fact]
    public void GetFulfillmentRates_UnknownBrand_ReturnsEmpty()
    {
        JsonElement result = Parse(_db.GetFulfillmentRates("NonExistentBrand99", null, null, 1));

        result.GetProperty("total_periods").GetInt32().Should().Be(0);
    }

    [Fact]
    public void GetFulfillmentRates_FiltersByRegion()
    {
        JsonElement result = Parse(_db.GetFulfillmentRates("Sierra Gold Tequila", "Northeast", null, 1));

        foreach (JsonElement item in result.GetProperty("rates").EnumerateArray())
        {
            item.GetProperty("region").GetString().Should().Contain("Northeast");
        }
    }

    [Fact]
    public void GetFulfillmentRates_SummaryIncludesTrend()
    {
        JsonElement result = Parse(_db.GetFulfillmentRates("Sierra Gold Tequila", null, null, 1));

        result.TryGetProperty("summary", out JsonElement summary).Should().BeTrue();
        string? trend = summary.GetProperty("trend").GetString();
        trend.Should().BeOneOf("improving", "declining", "stable");
    }

    #endregion

    #region GetSupplyHealthSummary

    [Theory]
    [InlineData("Sierra Gold Tequila")]
    [InlineData("FreshMart")]
    public void GetSupplyHealthSummary_ValidBrand_ReturnsAggregatedData(string brand)
    {
        JsonElement result = Parse(_db.GetSupplyHealthSummary(brand, null));

        result.GetProperty("brand").GetString().Should().Be(brand);
        result.TryGetProperty("overall_status", out _).Should().BeTrue(
            "health summary should include overall status");
        result.TryGetProperty("inventory_health", out _).Should().BeTrue(
            "health summary should include inventory health");
        result.TryGetProperty("fulfillment_health", out _).Should().BeTrue(
            "health summary should include fulfillment health");
    }

    [Fact]
    public void GetSupplyHealthSummary_UnknownBrand_ReturnsWithoutCrash()
    {
        // GetSupplyHealthSummary aggregates from sub-queries; unknown brand should
        // return zeroed-out summary, not throw
        Func<JsonElement> act = () => Parse(_db.GetSupplyHealthSummary("NonExistentBrand99", null));
        act.Should().NotThrow();
    }

    [Fact]
    public void GetSupplyHealthSummary_IncludesOverallStatus()
    {
        JsonElement result = Parse(_db.GetSupplyHealthSummary("Sierra Gold Tequila", null));

        result.TryGetProperty("overall_status", out JsonElement status).Should().BeTrue();
        string[] validStatuses = ["Green", "Yellow", "Red"];
        status.GetString().Should().BeOneOf(validStatuses);
    }

    [Fact]
    public void GetSupplyHealthSummary_WithRegion_FiltersCorrectly()
    {
        JsonElement result = Parse(_db.GetSupplyHealthSummary("Sierra Gold Tequila", "Northeast"));

        result.GetProperty("region").GetString().Should().Contain("Northeast");
    }

    #endregion
}

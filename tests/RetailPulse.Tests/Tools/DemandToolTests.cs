using FluentAssertions;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;
using System.Text.Json;

namespace RetailPulse.Tests.Tools;

/// <summary>
/// Tests for the demand forecasting data layer methods on RetailPulseDb:
/// GetHistoricalDemand, GenerateForecast, GetSeasonalityFactors, IdentifyDemandRisks.
/// Uses a real SQLite DB with seeded data from tenant.yaml.
/// </summary>
public class DemandToolTests : IDisposable
{
    private readonly string _dbPath;
    private readonly RetailPulseDb _db;

    public DemandToolTests()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");

        _dbPath = Path.Combine(Path.GetTempPath(), $"retailpulse_demand_test_{Guid.NewGuid():N}.db");
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

    #region GetHistoricalDemand

    [Theory]
    [InlineData("Sierra Gold Tequila", "Northeast")]
    [InlineData("Ridgeline Bourbon", "Southeast")]
    [InlineData("FreshMart", "Midwest")]
    [InlineData("Apex Grill", "West Coast")]
    public void GetHistoricalDemand_ValidBrandRegion_ReturnsData(string brand, string region)
    {
        var result = Parse(_db.GetHistoricalDemand(brand, region));

        result.TryGetProperty("error", out _).Should().BeFalse(
            $"should return data for brand '{brand}' in region '{region}'");
        result.GetProperty("summary").GetProperty("total_volume").GetDouble().Should().BeGreaterThan(0);
        result.GetProperty("weekly_data").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetHistoricalDemand_FiltersByBrand()
    {
        var result = Parse(_db.GetHistoricalDemand("Sierra Gold Tequila"));

        result.TryGetProperty("error", out _).Should().BeFalse();
        var weeklyData = result.GetProperty("weekly_data");
        weeklyData.GetArrayLength().Should().BeGreaterThan(0);

        foreach (var week in weeklyData.EnumerateArray())
        {
            week.GetProperty("brand").GetString().Should().Be("Sierra Gold Tequila");
        }
    }

    [Fact]
    public void GetHistoricalDemand_FiltersByRegion()
    {
        var result = Parse(_db.GetHistoricalDemand("Sierra Gold Tequila", "Northeast"));

        result.TryGetProperty("error", out _).Should().BeFalse();
        var weeklyData = result.GetProperty("weekly_data");
        weeklyData.GetArrayLength().Should().BeGreaterThan(0);

        foreach (var week in weeklyData.EnumerateArray())
        {
            week.GetProperty("region").GetString().Should().Be("Northeast");
        }
    }

    [Fact]
    public void GetHistoricalDemand_FiltersByChannel()
    {
        var result = Parse(_db.GetHistoricalDemand("Sierra Gold Tequila", null, "On-Premise"));

        result.TryGetProperty("error", out _).Should().BeFalse();
        var weeklyData = result.GetProperty("weekly_data");
        weeklyData.GetArrayLength().Should().BeGreaterThan(0);

        foreach (var week in weeklyData.EnumerateArray())
        {
            week.GetProperty("channel").GetString().Should().Be("On-Premise");
        }
    }

    [Fact]
    public void GetHistoricalDemand_DefaultMonths_Returns12MonthsOfData()
    {
        var result = Parse(_db.GetHistoricalDemand("Sierra Gold Tequila"));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("period").GetProperty("months").GetInt32().Should().Be(12);
        result.GetProperty("summary").GetProperty("weeks_of_data").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetHistoricalDemand_UnknownBrand_ReturnsEmptyNotCrash()
    {
        var result = Parse(_db.GetHistoricalDemand("Totally Fake Brand XYZ"));

        // Should not crash — returns summary with zero data
        result.GetProperty("summary").GetProperty("total_volume").GetDouble().Should().Be(0);
        result.GetProperty("summary").GetProperty("weeks_of_data").GetInt32().Should().Be(0);
    }

    [Fact]
    public void GetHistoricalDemand_NullBrand_ReturnsAllBrands()
    {
        var result = Parse(_db.GetHistoricalDemand(null));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("summary").GetProperty("total_volume").GetDouble().Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetHistoricalDemand_CustomMonths_Respected()
    {
        var result = Parse(_db.GetHistoricalDemand("Sierra Gold Tequila", months: 3));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("period").GetProperty("months").GetInt32().Should().Be(3);
    }

    #endregion

    #region GenerateForecast

    [Fact]
    public void GenerateForecast_ValidBrand_ReturnsCorrectDayCount()
    {
        var result = Parse(_db.GenerateForecast("Sierra Gold Tequila"));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("forecast_period").GetProperty("days").GetInt32().Should().Be(90);
        result.GetProperty("forecast").GetArrayLength().Should().Be(90);
    }

    [Fact]
    public void GenerateForecast_CustomDays_ReturnsRequestedCount()
    {
        var result = Parse(_db.GenerateForecast("Sierra Gold Tequila", days: 30));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("forecast_period").GetProperty("days").GetInt32().Should().Be(30);
        result.GetProperty("forecast").GetArrayLength().Should().Be(30);
    }

    [Fact]
    public void GenerateForecast_ConfidenceBoundsAreReasonable()
    {
        var result = Parse(_db.GenerateForecast("Sierra Gold Tequila"));

        result.TryGetProperty("error", out _).Should().BeFalse();
        var forecasts = result.GetProperty("forecast");

        foreach (var day in forecasts.EnumerateArray().Take(10))
        {
            var predicted = day.GetProperty("predicted_volume").GetDouble();
            var lower = day.GetProperty("confidence_lower").GetDouble();
            var upper = day.GetProperty("confidence_upper").GetDouble();

            lower.Should().BeLessThan(predicted, "lower bound should be below predicted");
            upper.Should().BeGreaterThan(predicted, "upper bound should be above predicted");
            lower.Should().BeGreaterThan(0, "lower bound should be positive");
        }
    }

    [Fact]
    public void GenerateForecast_SeasonalMultipliersApplied()
    {
        var result = Parse(_db.GenerateForecast("Sierra Gold Tequila"));

        result.TryGetProperty("error", out _).Should().BeFalse();

        var algorithm = result.GetProperty("algorithm");
        algorithm.GetProperty("category").GetString().Should().Be("Spirits");
        algorithm.GetProperty("method").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GenerateForecast_MissingBrand_ReturnsError()
    {
        var result = Parse(_db.GenerateForecast(""));

        result.TryGetProperty("error", out var error).Should().BeTrue();
        error.GetString().Should().Contain("brand");
    }

    [Fact]
    public void GenerateForecast_UnknownBrand_ReturnsError()
    {
        var result = Parse(_db.GenerateForecast("Totally Nonexistent Brand"));

        result.TryGetProperty("error", out _).Should().BeTrue();
    }

    [Fact]
    public void GenerateForecast_WithRegion_FiltersData()
    {
        var result = Parse(_db.GenerateForecast("Sierra Gold Tequila", "Northeast"));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("forecast").GetArrayLength().Should().Be(90);
    }

    [Theory]
    [InlineData("Sierra Gold Tequila")]
    [InlineData("FreshMart")]
    [InlineData("Apex Grill")]
    [InlineData("Pinnacle Hardware")]
    [InlineData("ClearDesk")]
    [InlineData("Urban Living")]
    public void GenerateForecast_AllCategories_ProduceForecast(string brand)
    {
        var result = Parse(_db.GenerateForecast(brand));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("forecast").GetArrayLength().Should().Be(90);
    }

    #endregion

    #region GetSeasonalityFactors

    [Theory]
    [InlineData("Spirits")]
    [InlineData("Grocery")]
    [InlineData("Quick-Serve Restaurant")]
    [InlineData("Home Improvement")]
    [InlineData("Office Supply")]
    [InlineData("Furniture")]
    public void GetSeasonalityFactors_KnownCategory_ReturnsFactors(string category)
    {
        var result = Parse(_db.GetSeasonalityFactors(category));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("factors").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetSeasonalityFactors_ValidMultiplierRange()
    {
        var result = Parse(_db.GetSeasonalityFactors("Spirits"));

        result.TryGetProperty("error", out _).Should().BeFalse();
        var factors = result.GetProperty("factors");

        foreach (var factor in factors.EnumerateArray())
        {
            var multiplier = factor.GetProperty("multiplier").GetDouble();
            multiplier.Should().BeInRange(0.5, 2.0,
                "seasonal multipliers should be in a reasonable range");
        }
    }

    [Fact]
    public void GetSeasonalityFactors_EachMonthHasValidValue()
    {
        var result = Parse(_db.GetSeasonalityFactors("Spirits"));

        result.TryGetProperty("error", out _).Should().BeFalse();
        var factors = result.GetProperty("factors");

        var months = factors.EnumerateArray()
            .Select(f => f.GetProperty("month").GetInt32())
            .ToList();

        months.Should().NotBeEmpty();
        months.Should().OnlyContain(m => m >= 1 && m <= 12);
    }

    [Fact]
    public void GetSeasonalityFactors_NullCategory_ReturnsAllCategories()
    {
        var result = Parse(_db.GetSeasonalityFactors(null));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("total_factors").GetInt32().Should().BeGreaterThan(12,
            "should return factors for multiple categories");

        var categories = result.GetProperty("categories").EnumerateArray()
            .Select(c => c.GetString())
            .ToList();

        categories.Should().Contain("Spirits");
        categories.Should().Contain("Grocery");
    }

    [Fact]
    public void GetSeasonalityFactors_UnknownCategory_ReturnsError()
    {
        var result = Parse(_db.GetSeasonalityFactors("Interstellar Travel"));

        result.TryGetProperty("error", out var error).Should().BeTrue();
        error.GetString().Should().Contain("Interstellar Travel");
    }

    [Fact]
    public void GetSeasonalityFactors_FactorsHaveImpactClassification()
    {
        var result = Parse(_db.GetSeasonalityFactors("Spirits"));

        result.TryGetProperty("error", out _).Should().BeFalse();
        var factors = result.GetProperty("factors");

        var validImpacts = new[] { "strong_boost", "moderate_boost", "baseline", "moderate_decline", "significant_decline" };

        foreach (var factor in factors.EnumerateArray())
        {
            var impact = factor.GetProperty("impact").GetString();
            validImpacts.Should().Contain(impact);
        }
    }

    #endregion

    #region IdentifyDemandRisks

    [Fact]
    public void IdentifyDemandRisks_ValidBrand_ReturnsRiskStructure()
    {
        var result = Parse(_db.IdentifyDemandRisks("Sierra Gold Tequila"));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("analysis_period").GetProperty("days").GetInt32().Should().Be(90);
        result.GetProperty("total_risks").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        result.GetProperty("risks").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public void IdentifyDemandRisks_ReturnsValidSeverityLevels()
    {
        var result = Parse(_db.IdentifyDemandRisks(null));

        var risks = result.GetProperty("risks");
        var validSeverities = new[] { "low", "medium", "high" };

        foreach (var risk in risks.EnumerateArray())
        {
            var severity = risk.GetProperty("severity").GetString();
            validSeverities.Should().Contain(severity,
                $"severity '{severity}' should be low, medium, or high");
        }
    }

    [Fact]
    public void IdentifyDemandRisks_DetectsAnomalies()
    {
        // The seeded data has injected anomalies — there should be at least some risks
        var result = Parse(_db.IdentifyDemandRisks(null));

        var risks = result.GetProperty("risks");
        risks.GetArrayLength().Should().BeGreaterThan(0,
            "seeded data includes anomalies that should be detected as risks");
    }

    [Fact]
    public void IdentifyDemandRisks_ReturnsArrayWhenNoRisks()
    {
        // Even if no risks for a specific filter, should return empty array
        var result = Parse(_db.IdentifyDemandRisks("Sierra Gold Tequila", "Northeast"));

        result.GetProperty("risks").ValueKind.Should().Be(JsonValueKind.Array);
        result.GetProperty("total_risks").GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void IdentifyDemandRisks_RiskSummaryHasCategories()
    {
        var result = Parse(_db.IdentifyDemandRisks(null));

        var summary = result.GetProperty("risk_summary");
        summary.GetProperty("high").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        summary.GetProperty("medium").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        summary.GetProperty("low").GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void IdentifyDemandRisks_RisksHaveAffectedPeriod()
    {
        var result = Parse(_db.IdentifyDemandRisks(null));

        var risks = result.GetProperty("risks");
        foreach (var risk in risks.EnumerateArray())
        {
            risk.GetProperty("affected_period").GetProperty("start").GetString()
                .Should().MatchRegex(@"\d{4}-\d{2}-\d{2}");
            risk.GetProperty("affected_period").GetProperty("end").GetString()
                .Should().MatchRegex(@"\d{4}-\d{2}-\d{2}");
        }
    }

    [Fact]
    public void IdentifyDemandRisks_WithRegionFilter_OnlyThatRegion()
    {
        var result = Parse(_db.IdentifyDemandRisks("Sierra Gold Tequila", "Northeast"));

        var risks = result.GetProperty("risks");
        foreach (var risk in risks.EnumerateArray())
        {
            risk.GetProperty("region").GetString().Should().Be("Northeast");
        }
    }

    [Fact]
    public void IdentifyDemandRisks_RisksAreSortedBySeverity()
    {
        var result = Parse(_db.IdentifyDemandRisks(null));

        var severityOrder = new Dictionary<string, int> { ["high"] = 0, ["medium"] = 1, ["low"] = 2 };
        var risks = result.GetProperty("risks").EnumerateArray().ToList();

        if (risks.Count > 1)
        {
            var severities = risks.Select(r =>
                severityOrder.GetValueOrDefault(r.GetProperty("severity").GetString()!, 3)).ToList();

            for (int i = 1; i < severities.Count; i++)
            {
                severities[i].Should().BeGreaterThanOrEqualTo(severities[i - 1],
                    "risks should be sorted by severity (high first)");
            }
        }
    }

    #endregion
}

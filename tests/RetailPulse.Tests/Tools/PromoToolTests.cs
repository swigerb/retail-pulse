using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;
using RetailPulse.Tests.TestInfrastructure;

namespace RetailPulse.Tests.Tools;

/// <summary>
/// Tests for the promo query data layer methods on RetailPulseDb:
/// GetPromoHistory, CalculateLift, EvaluateTiming, EstimateROI, GetPromoCalendar, GetPromoTypes.
/// Uses a real SQLite DB with seeded data from tenant.yaml.
/// </summary>
public class PromoToolTests : IDisposable
{
    private readonly string _dbPath;
    private readonly RetailPulseDb _db;

    public PromoToolTests()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");

        _dbPath = SqliteTestCleanup.NewDbPath("retailpulse_promo_test");
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

    private SqliteConnection OpenWritableConnection()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString());
        conn.Open();
        return conn;
    }

    #region GetPromoHistory

    [Fact]
    public void GetPromoHistory_NoFilters_ReturnsAllCampaigns()
    {
        JsonElement result = Parse(_db.GetPromoHistory(null, null, null, 24));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("total_campaigns").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetPromoHistory_ByBrand_FiltersCampaigns()
    {
        JsonElement result = Parse(_db.GetPromoHistory("Sierra Gold Tequila", null, null, 24));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("total_campaigns").GetInt32().Should().BeGreaterThan(0);

        JsonElement campaigns = result.GetProperty("campaigns");
        foreach (JsonElement campaign in campaigns.EnumerateArray())
        {
            campaign.GetProperty("brand").GetString().Should().Be("Sierra Gold Tequila");
        }
    }

    [Fact]
    public void GetPromoHistory_ByRegion_FiltersCampaigns()
    {
        JsonElement result = Parse(_db.GetPromoHistory(null, "Northeast", null, 24));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("total_campaigns").GetInt32().Should().BeGreaterThan(0);

        JsonElement campaigns = result.GetProperty("campaigns");
        foreach (JsonElement campaign in campaigns.EnumerateArray())
        {
            campaign.GetProperty("region").GetString().Should().Be("Northeast");
        }
    }

    [Fact]
    public void GetPromoHistory_ByPromoType_FiltersCampaigns()
    {
        JsonElement result = Parse(_db.GetPromoHistory(null, null, "BOGO", 24));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("total_campaigns").GetInt32().Should().BeGreaterThan(0);

        JsonElement campaigns = result.GetProperty("campaigns");
        foreach (JsonElement campaign in campaigns.EnumerateArray())
        {
            campaign.GetProperty("promo_type").GetString().Should().Be("BOGO");
        }
    }

    [Fact]
    public void GetPromoHistory_CombinedFilters_NarrowsResults()
    {
        JsonElement allResult = Parse(_db.GetPromoHistory(null, null, null, 24));
        JsonElement filteredResult = Parse(_db.GetPromoHistory("Sierra Gold Tequila", "Northeast", null, 24));

        filteredResult.TryGetProperty("error", out _).Should().BeFalse();
        filteredResult.GetProperty("total_campaigns").GetInt32()
            .Should().BeLessThanOrEqualTo(allResult.GetProperty("total_campaigns").GetInt32());
    }

    [Fact]
    public void GetPromoHistory_ReturnsCampaignFields()
    {
        JsonElement result = Parse(_db.GetPromoHistory(null, null, null, 24));

        result.TryGetProperty("error", out _).Should().BeFalse();
        JsonElement campaigns = result.GetProperty("campaigns");
        campaigns.GetArrayLength().Should().BeGreaterThan(0);

        JsonElement campaign = campaigns[0];
        campaign.TryGetProperty("brand", out _).Should().BeTrue();
        campaign.TryGetProperty("region", out _).Should().BeTrue();
        campaign.TryGetProperty("promo_type", out _).Should().BeTrue();
        campaign.TryGetProperty("campaign_name", out _).Should().BeTrue();
        campaign.TryGetProperty("start_date", out _).Should().BeTrue();
        campaign.TryGetProperty("end_date", out _).Should().BeTrue();
        campaign.TryGetProperty("spend", out _).Should().BeTrue();
        campaign.TryGetProperty("baseline_volume", out _).Should().BeTrue();
        campaign.TryGetProperty("actual_volume", out _).Should().BeTrue();
        campaign.TryGetProperty("lift_percent", out _).Should().BeTrue();
        campaign.TryGetProperty("roi", out _).Should().BeTrue();
        campaign.TryGetProperty("success_rating", out _).Should().BeTrue();
    }

    #endregion

    #region CalculateLift

    [Fact]
    public void CalculateLift_ValidInputs_ReturnsLiftEstimate()
    {
        JsonElement result = Parse(_db.CalculateLift("Sierra Gold Tequila", "Northeast", "BOGO", 50000));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("expected_lift_percent").GetDouble().Should().BeGreaterThan(0);
    }

    [Fact]
    public void CalculateLift_MissingBrand_ReturnsError()
    {
        JsonElement result = Parse(_db.CalculateLift("", "Northeast", "BOGO", 50000));

        result.TryGetProperty("error", out JsonElement error).Should().BeTrue();
        error.GetString().Should().Contain("brand");
    }

    [Fact]
    public void CalculateLift_MissingRegion_ReturnsError()
    {
        JsonElement result = Parse(_db.CalculateLift("Sierra Gold Tequila", "", "BOGO", 50000));

        result.TryGetProperty("error", out JsonElement error).Should().BeTrue();
        error.GetString().Should().Contain("region");
    }

    [Fact]
    public void CalculateLift_UnknownBrand_ReturnsError()
    {
        JsonElement result = Parse(_db.CalculateLift("NonExistentBrand", "Northeast", "BOGO", 50000));

        result.TryGetProperty("error", out _).Should().BeTrue();
    }

    [Fact]
    public void CalculateLift_HighSpend_ShowsDiminishingReturns()
    {
        JsonElement result = Parse(_db.CalculateLift("Sierra Gold Tequila", "Northeast", "BOGO", 999999));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("diminishing_returns").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void CalculateLift_ReturnsExpectedFields()
    {
        JsonElement result = Parse(_db.CalculateLift("Sierra Gold Tequila", "Northeast", "BOGO", 50000));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.TryGetProperty("brand", out _).Should().BeTrue();
        result.TryGetProperty("region", out _).Should().BeTrue();
        result.TryGetProperty("category", out _).Should().BeTrue();
        result.TryGetProperty("promo_type", out _).Should().BeTrue();
        result.TryGetProperty("spend", out _).Should().BeTrue();
        result.TryGetProperty("expected_lift_percent", out _).Should().BeTrue();
        result.TryGetProperty("confidence", out _).Should().BeTrue();
        result.TryGetProperty("similar_campaigns", out _).Should().BeTrue();
        result.TryGetProperty("diminishing_returns", out _).Should().BeTrue();
        result.TryGetProperty("spend_efficiency", out _).Should().BeTrue();
        result.TryGetProperty("coefficient_details", out _).Should().BeTrue();
    }

    #endregion

    #region EvaluateTiming

    [Fact]
    public void EvaluateTiming_ValidInputs_ReturnsTimingAnalysis()
    {
        JsonElement result = Parse(_db.EvaluateTiming("Sierra Gold Tequila", "Northeast",
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 28)));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.TryGetProperty("timing_score", out _).Should().BeTrue();
        result.TryGetProperty("recommendation", out _).Should().BeTrue();
    }

    [Fact]
    public void EvaluateTiming_MissingBrand_ReturnsError()
    {
        JsonElement result = Parse(_db.EvaluateTiming("", "Northeast",
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 28)));

        result.TryGetProperty("error", out JsonElement error).Should().BeTrue();
        error.GetString().Should().Contain("brand");
    }

    [Fact]
    public void EvaluateTiming_EndBeforeStart_ReturnsError()
    {
        JsonElement result = Parse(_db.EvaluateTiming("Sierra Gold Tequila", "Northeast",
            new DateOnly(2026, 6, 28), new DateOnly(2026, 6, 1)));

        result.TryGetProperty("error", out _).Should().BeTrue();
    }

    [Fact]
    public void EvaluateTiming_ReturnsTimingScore()
    {
        JsonElement result = Parse(_db.EvaluateTiming("Sierra Gold Tequila", "Northeast",
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 28)));

        result.TryGetProperty("error", out _).Should().BeFalse();
        double score = result.GetProperty("timing_score").GetDouble();
        score.Should().BeInRange(0.0, 1.0);
    }

    [Fact]
    public void EvaluateTiming_ReturnsRecommendation()
    {
        JsonElement result = Parse(_db.EvaluateTiming("Sierra Gold Tequila", "Northeast",
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 28)));

        result.TryGetProperty("error", out _).Should().BeFalse();
        string? recommendation = result.GetProperty("recommendation").GetString();
        recommendation.Should().BeOneOf(
            "Good timing",
            "Acceptable, review conflicts",
            "Poor timing, consider rescheduling");
    }

    #endregion

    #region EstimateROI

    [Fact]
    public void EstimateROI_ValidInputs_ReturnsRoiEstimate()
    {
        JsonElement result = Parse(_db.EstimateROI("Sierra Gold Tequila", "Northeast", "BOGO", 25000, 4));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.TryGetProperty("roi", out _).Should().BeTrue();
        result.GetProperty("roi").GetProperty("expected").GetDouble().Should().BeGreaterThan(0);
        result.GetProperty("roi").GetProperty("lower_bound").GetDouble().Should().BeGreaterThan(0);
        result.GetProperty("roi").GetProperty("upper_bound").GetDouble().Should().BeGreaterThan(0);
        result.GetProperty("break_even_days").GetInt32().Should().BeGreaterThan(0);
        result.GetProperty("similar_campaigns").GetInt32().Should().BeGreaterThan(0);
        result.GetProperty("historical_avg_roi").GetDouble().Should().BeGreaterThan(0);
    }

    [Fact]
    public void EstimateROI_MissingBrand_ReturnsError()
    {
        JsonElement result = Parse(_db.EstimateROI("", "Northeast", "BOGO", 100000, 4));

        result.TryGetProperty("error", out JsonElement error).Should().BeTrue();
        error.GetString().Should().Contain("brand");
    }

    [Fact]
    public void EstimateROI_InvalidDuration_ReturnsError()
    {
        JsonElement result = Parse(_db.EstimateROI("Sierra Gold Tequila", "Northeast", "BOGO", 100000, 0));

        result.TryGetProperty("error", out _).Should().BeTrue();
    }

    [Fact]
    public void EstimateROI_QuarterDuration_ReturnsRoiEstimate()
    {
        JsonElement result = Parse(_db.EstimateROI("Sierra Gold Tequila", "Northeast", "BOGO", 100000, 15));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("roi").GetProperty("expected").GetDouble().Should().BeGreaterThan(0);
        result.GetProperty("break_even_days").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public void EstimateROI_TargetLift_ChangesExpectedRoi()
    {
        JsonElement lowTarget = Parse(_db.EstimateROI("Sierra Gold Tequila", "Northeast", "BOGO", 25000, 4, 5));
        JsonElement highTarget = Parse(_db.EstimateROI("Sierra Gold Tequila", "Northeast", "BOGO", 25000, 4, 40));

        lowTarget.TryGetProperty("error", out _).Should().BeFalse();
        highTarget.TryGetProperty("error", out _).Should().BeFalse();
        highTarget.GetProperty("roi").GetProperty("expected").GetDouble()
            .Should().BeGreaterThan(lowTarget.GetProperty("roi").GetProperty("expected").GetDouble());
    }

    [Fact]
    public void EstimateROI_SubBreakeven_HasNoFiniteBreakEven()
    {
        JsonElement result = Parse(_db.EstimateROI("Ridgeline Bourbon", "Southeast", "Digital", 1_000_000, 1, 40));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("roi").GetProperty("expected").GetDouble().Should().BeLessThan(1.0);
        result.GetProperty("break_even_days").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void EstimateROI_NoHistory_ReturnsInsufficientHistory()
    {
        using SqliteConnection conn = OpenWritableConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM PromoHistory";
        cmd.ExecuteNonQuery();

        JsonElement result = Parse(_db.EstimateROI("Sierra Gold Tequila", "Northeast", "BOGO", 25000, 4));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("insufficient_history").GetBoolean().Should().BeTrue();
        result.GetProperty("similar_campaigns").GetInt32().Should().Be(0);
        result.TryGetProperty("roi", out _).Should().BeFalse();
    }

    [Fact]
    public void EstimateROI_HighSpend_FlagsApprovalRequired()
    {
        JsonElement result = Parse(_db.EstimateROI("Sierra Gold Tequila", "Northeast", "BOGO", 600000, 4));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("requires_approval").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void EstimateROI_ReturnsConfidenceInterval()
    {
        JsonElement result = Parse(_db.EstimateROI("Sierra Gold Tequila", "Northeast", "BOGO", 100000, 4));

        result.TryGetProperty("error", out _).Should().BeFalse();
        JsonElement roi = result.GetProperty("roi");
        double expected = roi.GetProperty("expected").GetDouble();
        double lower = roi.GetProperty("lower_bound").GetDouble();
        double upper = roi.GetProperty("upper_bound").GetDouble();

        lower.Should().BeLessThanOrEqualTo(expected, "lower bound should be <= expected");
        upper.Should().BeGreaterThanOrEqualTo(expected, "upper bound should be >= expected");
    }

    #endregion

    #region GetPromoCalendar

    [Fact]
    public void GetPromoCalendar_NoFilters_ReturnsCalendar()
    {
        JsonElement result = Parse(_db.GetPromoCalendar(null, null, 24));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("calendar").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetPromoCalendar_FilterByBrand_NarrowsResults()
    {
        JsonElement allResult = Parse(_db.GetPromoCalendar(null, null, 24));
        JsonElement filteredResult = Parse(_db.GetPromoCalendar("Sierra Gold Tequila", null, 24));

        filteredResult.TryGetProperty("error", out _).Should().BeFalse();
        filteredResult.GetProperty("calendar").GetArrayLength()
            .Should().BeLessThanOrEqualTo(allResult.GetProperty("calendar").GetArrayLength());
    }

    [Fact]
    public void GetPromoCalendar_ReturnsCalendarFields()
    {
        JsonElement result = Parse(_db.GetPromoCalendar(null, null, 24));

        result.TryGetProperty("error", out _).Should().BeFalse();
        JsonElement calendar = result.GetProperty("calendar");
        calendar.GetArrayLength().Should().BeGreaterThan(0);

        JsonElement entry = calendar[0];
        entry.TryGetProperty("brand", out _).Should().BeTrue();
        entry.TryGetProperty("region", out _).Should().BeTrue();
        entry.TryGetProperty("promo_type", out _).Should().BeTrue();
        entry.TryGetProperty("campaign", out _).Should().BeTrue();
        entry.TryGetProperty("start_date", out _).Should().BeTrue();
        entry.TryGetProperty("end_date", out _).Should().BeTrue();
        entry.TryGetProperty("spend", out _).Should().BeTrue();
        entry.TryGetProperty("roi", out _).Should().BeTrue();
    }

    #endregion

    #region GetPromoTypes

    [Fact]
    public void GetPromoTypes_ReturnsAllTypes()
    {
        JsonElement result = Parse(_db.GetPromoTypes());

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("promo_types").GetArrayLength().Should().Be(5);
    }

    [Fact]
    public void GetPromoTypes_ContainsExpectedTypes()
    {
        JsonElement result = Parse(_db.GetPromoTypes());

        result.TryGetProperty("error", out _).Should().BeFalse();
        var codes = result.GetProperty("promo_types").EnumerateArray()
            .Select(t => t.GetProperty("code").GetString()?.ToLowerInvariant())
            .ToList();

        codes.Should().Contain("bogo");
        codes.Should().Contain("discount");
        codes.Should().Contain("display");
        codes.Should().Contain("digital");
        codes.Should().Contain("bundle");
    }

    #endregion
}

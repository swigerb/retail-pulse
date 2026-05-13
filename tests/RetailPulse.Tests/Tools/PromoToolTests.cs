using FluentAssertions;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;
using System.Text.Json;

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
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");

        _dbPath = Path.Combine(Path.GetTempPath(), $"retailpulse_promo_test_{Guid.NewGuid():N}.db");
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

    #region GetPromoHistory

    [Fact]
    public void GetPromoHistory_NoFilters_ReturnsAllCampaigns()
    {
        var result = Parse(_db.GetPromoHistory(null, null, null, 24));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("total_campaigns").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetPromoHistory_ByBrand_FiltersCampaigns()
    {
        var result = Parse(_db.GetPromoHistory("Sierra Gold Tequila", null, null, 24));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("total_campaigns").GetInt32().Should().BeGreaterThan(0);

        var campaigns = result.GetProperty("campaigns");
        foreach (var campaign in campaigns.EnumerateArray())
        {
            campaign.GetProperty("brand").GetString().Should().Be("Sierra Gold Tequila");
        }
    }

    [Fact]
    public void GetPromoHistory_ByRegion_FiltersCampaigns()
    {
        var result = Parse(_db.GetPromoHistory(null, "Northeast", null, 24));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("total_campaigns").GetInt32().Should().BeGreaterThan(0);

        var campaigns = result.GetProperty("campaigns");
        foreach (var campaign in campaigns.EnumerateArray())
        {
            campaign.GetProperty("region").GetString().Should().Be("Northeast");
        }
    }

    [Fact]
    public void GetPromoHistory_ByPromoType_FiltersCampaigns()
    {
        var result = Parse(_db.GetPromoHistory(null, null, "BOGO", 24));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("total_campaigns").GetInt32().Should().BeGreaterThan(0);

        var campaigns = result.GetProperty("campaigns");
        foreach (var campaign in campaigns.EnumerateArray())
        {
            campaign.GetProperty("promo_type").GetString().Should().Be("BOGO");
        }
    }

    [Fact]
    public void GetPromoHistory_CombinedFilters_NarrowsResults()
    {
        var allResult = Parse(_db.GetPromoHistory(null, null, null, 24));
        var filteredResult = Parse(_db.GetPromoHistory("Sierra Gold Tequila", "Northeast", null, 24));

        filteredResult.TryGetProperty("error", out _).Should().BeFalse();
        filteredResult.GetProperty("total_campaigns").GetInt32()
            .Should().BeLessThanOrEqualTo(allResult.GetProperty("total_campaigns").GetInt32());
    }

    [Fact]
    public void GetPromoHistory_ReturnsCampaignFields()
    {
        var result = Parse(_db.GetPromoHistory(null, null, null, 24));

        result.TryGetProperty("error", out _).Should().BeFalse();
        var campaigns = result.GetProperty("campaigns");
        campaigns.GetArrayLength().Should().BeGreaterThan(0);

        var campaign = campaigns[0];
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
        var result = Parse(_db.CalculateLift("Sierra Gold Tequila", "Northeast", "BOGO", 50000));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("expected_lift_percent").GetDouble().Should().BeGreaterThan(0);
    }

    [Fact]
    public void CalculateLift_MissingBrand_ReturnsError()
    {
        var result = Parse(_db.CalculateLift("", "Northeast", "BOGO", 50000));

        result.TryGetProperty("error", out var error).Should().BeTrue();
        error.GetString().Should().Contain("brand");
    }

    [Fact]
    public void CalculateLift_MissingRegion_ReturnsError()
    {
        var result = Parse(_db.CalculateLift("Sierra Gold Tequila", "", "BOGO", 50000));

        result.TryGetProperty("error", out var error).Should().BeTrue();
        error.GetString().Should().Contain("region");
    }

    [Fact]
    public void CalculateLift_UnknownBrand_ReturnsError()
    {
        var result = Parse(_db.CalculateLift("NonExistentBrand", "Northeast", "BOGO", 50000));

        result.TryGetProperty("error", out _).Should().BeTrue();
    }

    [Fact]
    public void CalculateLift_HighSpend_ShowsDiminishingReturns()
    {
        var result = Parse(_db.CalculateLift("Sierra Gold Tequila", "Northeast", "BOGO", 999999));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("diminishing_returns").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void CalculateLift_ReturnsExpectedFields()
    {
        var result = Parse(_db.CalculateLift("Sierra Gold Tequila", "Northeast", "BOGO", 50000));

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
        var result = Parse(_db.EvaluateTiming("Sierra Gold Tequila", "Northeast",
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 28)));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.TryGetProperty("timing_score", out _).Should().BeTrue();
        result.TryGetProperty("recommendation", out _).Should().BeTrue();
    }

    [Fact]
    public void EvaluateTiming_MissingBrand_ReturnsError()
    {
        var result = Parse(_db.EvaluateTiming("", "Northeast",
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 28)));

        result.TryGetProperty("error", out var error).Should().BeTrue();
        error.GetString().Should().Contain("brand");
    }

    [Fact]
    public void EvaluateTiming_EndBeforeStart_ReturnsError()
    {
        var result = Parse(_db.EvaluateTiming("Sierra Gold Tequila", "Northeast",
            new DateOnly(2026, 6, 28), new DateOnly(2026, 6, 1)));

        result.TryGetProperty("error", out _).Should().BeTrue();
    }

    [Fact]
    public void EvaluateTiming_ReturnsTimingScore()
    {
        var result = Parse(_db.EvaluateTiming("Sierra Gold Tequila", "Northeast",
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 28)));

        result.TryGetProperty("error", out _).Should().BeFalse();
        var score = result.GetProperty("timing_score").GetDouble();
        score.Should().BeInRange(0.0, 1.0);
    }

    [Fact]
    public void EvaluateTiming_ReturnsRecommendation()
    {
        var result = Parse(_db.EvaluateTiming("Sierra Gold Tequila", "Northeast",
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 28)));

        result.TryGetProperty("error", out _).Should().BeFalse();
        var recommendation = result.GetProperty("recommendation").GetString();
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
        var result = Parse(_db.EstimateROI("Sierra Gold Tequila", "Northeast", "BOGO", 100000, 4));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.TryGetProperty("roi", out _).Should().BeTrue();
    }

    [Fact]
    public void EstimateROI_MissingBrand_ReturnsError()
    {
        var result = Parse(_db.EstimateROI("", "Northeast", "BOGO", 100000, 4));

        result.TryGetProperty("error", out var error).Should().BeTrue();
        error.GetString().Should().Contain("brand");
    }

    [Fact]
    public void EstimateROI_InvalidDuration_ReturnsError()
    {
        var result = Parse(_db.EstimateROI("Sierra Gold Tequila", "Northeast", "BOGO", 100000, 0));

        result.TryGetProperty("error", out _).Should().BeTrue();
    }

    [Fact]
    public void EstimateROI_InvalidDuration_Over12_ReturnsError()
    {
        var result = Parse(_db.EstimateROI("Sierra Gold Tequila", "Northeast", "BOGO", 100000, 15));

        result.TryGetProperty("error", out _).Should().BeTrue();
    }

    [Fact]
    public void EstimateROI_HighSpend_FlagsApprovalRequired()
    {
        var result = Parse(_db.EstimateROI("Sierra Gold Tequila", "Northeast", "BOGO", 600000, 4));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("requires_approval").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void EstimateROI_ReturnsConfidenceInterval()
    {
        var result = Parse(_db.EstimateROI("Sierra Gold Tequila", "Northeast", "BOGO", 100000, 4));

        result.TryGetProperty("error", out _).Should().BeFalse();
        var roi = result.GetProperty("roi");
        var expected = roi.GetProperty("expected").GetDouble();
        var lower = roi.GetProperty("lower_bound").GetDouble();
        var upper = roi.GetProperty("upper_bound").GetDouble();

        lower.Should().BeLessThanOrEqualTo(expected, "lower bound should be <= expected");
        upper.Should().BeGreaterThanOrEqualTo(expected, "upper bound should be >= expected");
    }

    #endregion

    #region GetPromoCalendar

    [Fact]
    public void GetPromoCalendar_NoFilters_ReturnsCalendar()
    {
        var result = Parse(_db.GetPromoCalendar(null, null, 24));

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("calendar").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetPromoCalendar_FilterByBrand_NarrowsResults()
    {
        var allResult = Parse(_db.GetPromoCalendar(null, null, 24));
        var filteredResult = Parse(_db.GetPromoCalendar("Sierra Gold Tequila", null, 24));

        filteredResult.TryGetProperty("error", out _).Should().BeFalse();
        filteredResult.GetProperty("calendar").GetArrayLength()
            .Should().BeLessThanOrEqualTo(allResult.GetProperty("calendar").GetArrayLength());
    }

    [Fact]
    public void GetPromoCalendar_ReturnsCalendarFields()
    {
        var result = Parse(_db.GetPromoCalendar(null, null, 24));

        result.TryGetProperty("error", out _).Should().BeFalse();
        var calendar = result.GetProperty("calendar");
        calendar.GetArrayLength().Should().BeGreaterThan(0);

        var entry = calendar[0];
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
        var result = Parse(RetailPulseDb.GetPromoTypes());

        result.TryGetProperty("error", out _).Should().BeFalse();
        result.GetProperty("promo_types").GetArrayLength().Should().Be(5);
    }

    [Fact]
    public void GetPromoTypes_ContainsExpectedTypes()
    {
        var result = Parse(RetailPulseDb.GetPromoTypes());

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

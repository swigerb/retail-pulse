using FluentAssertions;
using RetailPulse.Api.Alerts;
using RetailPulse.Contracts.Alerts;

namespace RetailPulse.Tests.Alerts;

/// <summary>
/// Tests for IAlertService anomaly detection logic.
/// Covers: demand spike, supply drop, trend reversal, thresholds, severity, alert structure.
/// 20+ tests.
/// </summary>
public class AlertServiceTests
{
    private static InMemoryAlertService CreateService(TimeSpan? throttleWindow = null)
        => new(throttleWindow ?? TimeSpan.FromHours(1));

    #region Demand Spike Detection (>20% above baseline)

    [Fact]
    public async Task CheckForAlerts_DemandSpike25Percent_DetectsAlert()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand X", "Northeast", "demand_spike", baseline: 1000, current: 1250);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().ContainSingle();
        alerts[0].Type.Should().Be("demand_spike");
        alerts[0].Brand.Should().Be("Brand X");
        alerts[0].Region.Should().Be("Northeast");
    }

    [Fact]
    public async Task CheckForAlerts_DemandSpike50Percent_DetectsHighSeverity()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand A", "West", "demand_spike", baseline: 1000, current: 1500);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().ContainSingle();
        alerts[0].Severity.Should().Be("high");
    }

    [Fact]
    public async Task CheckForAlerts_DemandSpike30Percent_DetectsMediumSeverity()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand B", "South", "demand_spike", baseline: 1000, current: 1300);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().ContainSingle();
        alerts[0].Severity.Should().Be("medium");
    }

    [Theory]
    [InlineData(1000, 1190)] // 19% — below threshold
    [InlineData(1000, 1100)] // 10% — well below
    [InlineData(1000, 1200)] // exactly 20% — not above
    public async Task CheckForAlerts_DemandBelowThreshold_NoAlert(double baseline, double current)
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand C", "Midwest", "demand_spike", baseline, current);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().BeEmpty("demand must be ABOVE 20% threshold, not equal to or below");
    }

    [Fact]
    public async Task CheckForAlerts_DemandSpike21Percent_FiresAlert()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand D", "Southeast", "demand_spike", baseline: 1000, current: 1210);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().ContainSingle();
    }

    #endregion

    #region Supply Drop Detection (>15% below baseline)

    [Fact]
    public async Task CheckForAlerts_SupplyDrop20Percent_DetectsAlert()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand E", "West", "supply_drop", baseline: 1000, current: 800);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().ContainSingle();
        alerts[0].Type.Should().Be("supply_drop");
    }

    [Fact]
    public async Task CheckForAlerts_SupplyDrop45Percent_DetectsHighSeverity()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand F", "Northeast", "supply_drop", baseline: 1000, current: 550);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().ContainSingle();
        alerts[0].Severity.Should().Be("high");
    }

    [Theory]
    [InlineData(1000, 860)] // 14% — below threshold
    [InlineData(1000, 900)] // 10% — well below
    [InlineData(1000, 850)] // exactly 15% — not above
    public async Task CheckForAlerts_SupplyDropBelowThreshold_NoAlert(double baseline, double current)
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand G", "South", "supply_drop", baseline, current);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().BeEmpty("supply drop must exceed 15% threshold");
    }

    [Fact]
    public async Task CheckForAlerts_SupplyDrop16Percent_FiresAlert()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand H", "Midwest", "supply_drop", baseline: 1000, current: 840);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().ContainSingle();
    }

    #endregion

    #region Trend Reversal Detection (>10% direction change)

    [Fact]
    public async Task CheckForAlerts_TrendReversal15Percent_DetectsAlert()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand I", "Southeast", "trend_reversal", baseline: 1000, current: 1150);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().ContainSingle();
        alerts[0].Type.Should().Be("trend_reversal");
    }

    [Fact]
    public async Task CheckForAlerts_TrendReversal25Percent_MediumSeverity()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand J", "West", "trend_reversal", baseline: 1000, current: 750);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().ContainSingle();
        alerts[0].Severity.Should().Be("medium");
    }

    [Theory]
    [InlineData(1000, 1090)] // 9% — below threshold
    [InlineData(1000, 950)]  // 5% — well below
    [InlineData(1000, 1100)] // exactly 10% — not above
    public async Task CheckForAlerts_TrendReversalBelowThreshold_NoAlert(double baseline, double current)
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand K", "Northeast", "trend_reversal", baseline, current);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().BeEmpty("trend reversal must exceed 10% threshold");
    }

    #endregion

    #region Severity Classification

    [Theory]
    [InlineData(1000, 1450, "high")]    // 45% demand spike
    [InlineData(1000, 1250, "medium")]  // 25% demand spike
    [InlineData(1000, 500, "high")]     // 50% supply drop
    [InlineData(1000, 780, "medium")]   // 22% supply drop
    public async Task CheckForAlerts_SeverityClassification_CorrectByDeviation(
        double baseline, double current, string expectedSeverity)
    {
        var svc = CreateService();
        var type = current > baseline ? "demand_spike" : "supply_drop";
        svc.SeedDataPoint("TestBrand", "TestRegion", type, baseline, current);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().ContainSingle();
        alerts[0].Severity.Should().Be(expectedSeverity);
    }

    #endregion

    #region Alert Structure

    [Fact]
    public async Task CheckForAlerts_AlertHasAllFieldsPopulated()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Apex Grill", "Southwest", "demand_spike", baseline: 1000, current: 1500);

        var alerts = await svc.CheckForAlertsAsync();

        var alert = alerts.Should().ContainSingle().Subject;
        alert.Id.Should().NotBeNullOrEmpty();
        alert.Type.Should().NotBeNullOrEmpty();
        alert.Severity.Should().NotBeNullOrEmpty();
        alert.Title.Should().NotBeNullOrEmpty();
        alert.Description.Should().NotBeNullOrEmpty();
        alert.Brand.Should().Be("Apex Grill");
        alert.Region.Should().Be("Southwest");
        alert.RecommendedAction.Should().NotBeNullOrEmpty();
        alert.DetectedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CheckForAlerts_AlertMetadata_ContainsDeviationPercent()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand M", "West", "demand_spike", baseline: 1000, current: 1400);

        var alerts = await svc.CheckForAlertsAsync();

        alerts[0].Metadata.Should().NotBeNull();
        alerts[0].Metadata.Should().ContainKey("deviationPercent");
        ((double)alerts[0].Metadata!["deviationPercent"]).Should().BeApproximately(40.0, 0.1);
    }

    [Fact]
    public async Task CheckForAlerts_MultipleAnomalies_ReturnsAllAlerts()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand N", "West", "demand_spike", baseline: 1000, current: 1300);
        svc.SeedDataPoint("Brand O", "East", "supply_drop", baseline: 1000, current: 800);
        svc.SeedDataPoint("Brand P", "South", "trend_reversal", baseline: 1000, current: 1200);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().HaveCount(3);
        alerts.Select(a => a.Type).Should().BeEquivalentTo(
            ["demand_spike", "supply_drop", "trend_reversal"]);
    }

    [Fact]
    public async Task CheckForAlerts_NoAnomalies_ReturnsEmpty()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand Q", "West", "demand_spike", baseline: 1000, current: 1100);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckForAlerts_UniqueIdsPerAlert()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand R", "East", "demand_spike", baseline: 1000, current: 1500);
        svc.SeedDataPoint("Brand S", "West", "supply_drop", baseline: 1000, current: 500);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().HaveCount(2);
        alerts[0].Id.Should().NotBe(alerts[1].Id);
    }

    [Fact]
    public async Task CheckForAlerts_AlertDescription_ContainsDeviationInfo()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand T", "North", "demand_spike", baseline: 1000, current: 1350);

        var alerts = await svc.CheckForAlertsAsync();

        alerts[0].Description.Should().Contain("35");
    }

    #endregion
}

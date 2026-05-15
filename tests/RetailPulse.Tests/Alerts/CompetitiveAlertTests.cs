using FluentAssertions;
using RetailPulse.Api.Alerts;
using RetailPulse.Contracts.Alerts;

namespace RetailPulse.Tests.Alerts;

/// <summary>
/// Tests for competitive intelligence alert scenarios using the existing
/// InMemoryAlertService anomaly detection framework. Verifies that competitive
/// market changes trigger appropriate alerts via demand_spike, supply_drop,
/// and trend_reversal types (which capture competitive pressure signals).
/// 
/// Sprint 2.2 test-first: validates the alerting infrastructure supports
/// competitive intelligence use cases before dedicated competitive alert types
/// are added to the service.
/// </summary>
public class CompetitiveAlertTests
{
    private static InMemoryAlertService CreateService(TimeSpan? throttleWindow = null)
        => new(throttleWindow ?? TimeSpan.FromHours(1));

    #region Competitive Price Pressure → Demand Spike Detection

    [Fact]
    public async Task CompetitorPriceDrop_CausesDemandSpike_TriggersAlert()
    {
        var svc = CreateService();
        // When a competitor drops prices, our demand may spike unexpectedly as customers shift
        svc.SeedDataPoint("Sierra Gold Tequila", "Northeast", "demand_spike",
            baseline: 1000, current: 1350);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().ContainSingle(a => a.Type == "demand_spike",
            "a >20% demand spike (competitor pricing pressure) should trigger an alert");
        var alert = alerts.First(a => a.Type == "demand_spike");
        alert.Brand.Should().Be("Sierra Gold Tequila");
        alert.Region.Should().Be("Northeast");
    }

    [Fact]
    public async Task CompetitorPriceDrop_ModerateImpact_NoAlert()
    {
        var svc = CreateService();
        // 15% demand increase — below the 20% spike threshold
        svc.SeedDataPoint("Ridgeline Bourbon", "Southeast", "demand_spike",
            baseline: 1000, current: 1150);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Where(a => a.Type == "demand_spike" && a.Brand == "Ridgeline Bourbon")
            .Should().BeEmpty("15% demand increase is below the 20% spike threshold");
    }

    [Fact]
    public async Task CompetitorPriceDrop_LargeImpact_IsHighSeverity()
    {
        var svc = CreateService();
        // 50% demand spike — extreme competitive disruption
        svc.SeedDataPoint("Summit Vodka", "Midwest", "demand_spike",
            baseline: 1000, current: 1500);

        var alerts = await svc.CheckForAlertsAsync();

        var alert = alerts.First(a => a.Type == "demand_spike");
        alert.Severity.Should().Be("high",
            "50% demand spike should be classified as high severity");
    }

    #endregion

    #region Market Share Loss → Supply Drop Detection

    [Fact]
    public async Task MarketShareLoss_CausesSupplyDrop_TriggersAlert()
    {
        var svc = CreateService();
        // When losing market share, our supply/volume drops significantly
        svc.SeedDataPoint("FreshMart", "West Coast", "supply_drop",
            baseline: 5000, current: 4000);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().Contain(a => a.Type == "supply_drop",
            "a >15% supply drop (market share erosion) should trigger an alert");
    }

    [Fact]
    public async Task MarketShareLoss_Marginal_DoesNotTrigger()
    {
        var svc = CreateService();
        // 10% supply drop — below the 15% threshold
        svc.SeedDataPoint("Harvest Table", "Southwest", "supply_drop",
            baseline: 5000, current: 4500);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Where(a => a.Type == "supply_drop" && a.Brand == "Harvest Table")
            .Should().BeEmpty("10% supply drop is below the 15% threshold");
    }

    [Fact]
    public async Task MarketShareLoss_Severe_IsMediumOrHighSeverity()
    {
        var svc = CreateService();
        // 30% supply drop — significant competitive loss
        svc.SeedDataPoint("Apex Grill", "Northeast", "supply_drop",
            baseline: 5000, current: 3500);

        var alerts = await svc.CheckForAlertsAsync();

        var alert = alerts.First(a => a.Type == "supply_drop");
        alert.Severity.Should().BeOneOf("high", "medium",
            "a 30% supply drop should be medium or high severity");
    }

    #endregion

    #region Competitive Trend Reversal Detection

    [Fact]
    public async Task CompetitorEntry_CausesTrendReversal_TriggersAlert()
    {
        var svc = CreateService();
        // Competitor entry causes our trend to reverse by >10%
        svc.SeedDataPoint("Sierra Gold Tequila", "Southwest", "trend_reversal",
            baseline: 1000, current: 850);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().Contain(a => a.Type == "trend_reversal",
            "a >10% trend reversal (competitive disruption) should trigger an alert");
    }

    [Fact]
    public async Task TrendReversal_HasBrandAndRegion()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Ridgeline Bourbon", "Southeast", "trend_reversal",
            baseline: 1000, current: 800);

        var alerts = await svc.CheckForAlertsAsync();

        var alert = alerts.First(a => a.Type == "trend_reversal");
        alert.Brand.Should().Be("Ridgeline Bourbon");
        alert.Region.Should().Be("Southeast");
    }

    #endregion

    #region Alert Metadata and Recommendations

    [Fact]
    public async Task Alert_IncludesRecommendedAction()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Summit Vodka", "Northeast", "demand_spike",
            baseline: 1000, current: 1500);

        var alerts = await svc.CheckForAlertsAsync();

        var alert = alerts.First(a => a.Type == "demand_spike");
        alert.RecommendedAction.Should().NotBeNullOrWhiteSpace(
            "alerts should include a recommended action");
    }

    [Fact]
    public async Task Alert_HasDescriptiveTitle()
    {
        var svc = CreateService();
        svc.SeedDataPoint("FreshMart", "Midwest", "supply_drop",
            baseline: 5000, current: 3000);

        var alerts = await svc.CheckForAlertsAsync();

        var alert = alerts.First(a => a.Type == "supply_drop");
        alert.Title.Should().NotBeNullOrWhiteSpace(
            "alert should have a descriptive title");
        alert.Title.Should().Contain("FreshMart",
            "title should reference the affected brand");
    }

    #endregion

    #region Severity Classification

    [Fact]
    public async Task Severity_HighDeviation_IsHigh()
    {
        var svc = CreateService();
        // >40% deviation → high severity
        svc.SeedDataPoint("ClearDesk", "Northeast", "demand_spike",
            baseline: 1000, current: 1500);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.First(a => a.Type == "demand_spike").Severity.Should().Be("high",
            ">40% deviation should classify as high severity");
    }

    [Fact]
    public async Task Severity_ModerateDeviation_IsMedium()
    {
        var svc = CreateService();
        // 25% deviation → medium severity (between 20-40%)
        svc.SeedDataPoint("Urban Living", "Southeast", "demand_spike",
            baseline: 1000, current: 1250);

        var alerts = await svc.CheckForAlertsAsync();

        var alert = alerts.First(a => a.Type == "demand_spike");
        alert.Severity.Should().Be("medium",
            "25% deviation should be medium severity");
    }

    [Fact]
    public async Task Severity_SupplyDropAboveThreshold_GeneratesAlert()
    {
        var svc = CreateService();
        // 25% supply drop — should generate alert with medium or higher severity
        svc.SeedDataPoint("Foundry Home", "Midwest", "supply_drop",
            baseline: 1000, current: 750);

        var alerts = await svc.CheckForAlertsAsync();

        var alert = alerts.FirstOrDefault(a => a.Type == "supply_drop" && a.Brand == "Foundry Home");
        alert.Should().NotBeNull("25% supply drop is above the 15% threshold");
        alert.Severity.Should().BeOneOf("medium", "high",
            "25% deviation should be medium or high severity");
    }

    #endregion
}

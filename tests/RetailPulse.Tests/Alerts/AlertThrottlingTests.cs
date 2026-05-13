using FluentAssertions;
using RetailPulse.Api.Alerts;

namespace RetailPulse.Tests.Alerts;

/// <summary>
/// Tests for alert throttling: max 1 alert per (type, brand, region) per hour.
/// Covers: throttle prevention, independent combos, expiry, persistence across cycles.
/// 10+ tests.
/// </summary>
public class AlertThrottlingTests
{
    private InMemoryAlertService CreateService(TimeSpan? throttleWindow = null)
        => new(throttleWindow ?? TimeSpan.FromHours(1));

    [Fact]
    public async Task Throttle_PreventsDuplicateAlertWithinWindow()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand A", "West", "demand_spike", baseline: 1000, current: 1300);

        var first = await svc.CheckForAlertsAsync();
        first.Should().ContainSingle();

        // Second check within throttle window — should be suppressed
        var second = await svc.CheckForAlertsAsync();
        second.Should().BeEmpty("throttle window hasn't expired");
    }

    [Fact]
    public async Task Throttle_DifferentTypeSameBrandRegion_FiresIndependently()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand B", "East", "demand_spike", baseline: 1000, current: 1300);
        svc.SeedDataPoint("Brand B", "East", "supply_drop", baseline: 1000, current: 800);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().HaveCount(2);
        alerts.Select(a => a.Type).Should().Contain("demand_spike");
        alerts.Select(a => a.Type).Should().Contain("supply_drop");
    }

    [Fact]
    public async Task Throttle_SameTypeDifferentBrand_FiresIndependently()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand C", "West", "demand_spike", baseline: 1000, current: 1300);
        svc.SeedDataPoint("Brand D", "West", "demand_spike", baseline: 1000, current: 1400);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().HaveCount(2);
        alerts.Select(a => a.Brand).Should().BeEquivalentTo(new[] { "Brand C", "Brand D" });
    }

    [Fact]
    public async Task Throttle_SameTypeSameBrandDifferentRegion_FiresIndependently()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand E", "West", "demand_spike", baseline: 1000, current: 1300);
        svc.SeedDataPoint("Brand E", "East", "demand_spike", baseline: 1000, current: 1300);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().HaveCount(2);
        alerts.Select(a => a.Region).Should().BeEquivalentTo(new[] { "West", "East" });
    }

    [Fact]
    public async Task Throttle_AfterWindowExpires_AlertCanFireAgain()
    {
        // Use 1-second throttle window for testing
        var svc = CreateService(throttleWindow: TimeSpan.FromMilliseconds(100));
        svc.SeedDataPoint("Brand F", "South", "demand_spike", baseline: 1000, current: 1300);

        var first = await svc.CheckForAlertsAsync();
        first.Should().ContainSingle();

        // Wait for throttle to expire
        await Task.Delay(150);

        var second = await svc.CheckForAlertsAsync();
        second.Should().ContainSingle("throttle window has expired");
    }

    [Fact]
    public async Task Throttle_StatePersistsAcrossCheckCycles()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand G", "North", "demand_spike", baseline: 1000, current: 1300);

        // First cycle fires
        await svc.CheckForAlertsAsync();
        // Second cycle suppressed
        var second = await svc.CheckForAlertsAsync();
        // Third cycle still suppressed
        var third = await svc.CheckForAlertsAsync();

        second.Should().BeEmpty();
        third.Should().BeEmpty("throttle state persists across check cycles");
    }

    [Fact]
    public void IsThrottled_AfterAlert_ReturnsTrue()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand H", "West", "demand_spike", baseline: 1000, current: 1300);
        svc.CheckForAlertsAsync().Wait();

        svc.IsThrottled("demand_spike", "Brand H", "West").Should().BeTrue();
    }

    [Fact]
    public void IsThrottled_NoAlert_ReturnsFalse()
    {
        var svc = CreateService();
        svc.IsThrottled("demand_spike", "Brand I", "East").Should().BeFalse();
    }

    [Fact]
    public void ResetThrottle_AllowsAlertToFireAgain()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand J", "West", "demand_spike", baseline: 1000, current: 1300);
        svc.CheckForAlertsAsync().Wait();

        svc.IsThrottled("demand_spike", "Brand J", "West").Should().BeTrue();
        svc.ResetThrottle("demand_spike", "Brand J", "West");
        svc.IsThrottled("demand_spike", "Brand J", "West").Should().BeFalse();
    }

    [Fact]
    public async Task Throttle_ManualTimestampExpiry_AlertFires()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand K", "East", "supply_drop", baseline: 1000, current: 800);

        // Fire once
        await svc.CheckForAlertsAsync();

        // Set throttle timestamp to 2 hours ago (expired)
        svc.SetThrottleTimestamp("supply_drop", "Brand K", "East",
            DateTimeOffset.UtcNow.AddHours(-2));

        // Should fire again
        var alerts = await svc.CheckForAlertsAsync();
        alerts.Should().ContainSingle();
    }

    [Fact]
    public async Task Throttle_ThreeTypeSameLocation_AllFireIndependently()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand L", "Central", "demand_spike", baseline: 1000, current: 1300);
        svc.SeedDataPoint("Brand L", "Central", "supply_drop", baseline: 1000, current: 800);
        svc.SeedDataPoint("Brand L", "Central", "trend_reversal", baseline: 1000, current: 1200);

        var alerts = await svc.CheckForAlertsAsync();

        alerts.Should().HaveCount(3, "each (type, brand, region) is independently throttled");
    }
}

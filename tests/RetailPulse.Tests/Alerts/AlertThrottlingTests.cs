using FluentAssertions;
using RetailPulse.Api.Alerts;
using RetailPulse.Contracts.Alerts;

namespace RetailPulse.Tests.Alerts;

/// <summary>
/// Tests for alert throttling: max 1 alert per (type, brand, region) per hour.
/// Covers: throttle prevention, independent combos, expiry, persistence across cycles.
/// 10+ tests.
/// </summary>
public class AlertThrottlingTests
{
    private static InMemoryAlertService CreateService(TimeSpan? throttleWindow = null)
        => new(throttleWindow ?? TimeSpan.FromHours(1));

    [Fact]
    public async Task Throttle_PreventsDuplicateAlertWithinWindow()
    {
        InMemoryAlertService svc = CreateService();
        svc.SeedDataPoint("Brand A", "West", "demand_spike", baseline: 1000, current: 1300);

        IReadOnlyList<Alert> first = await svc.CheckForAlertsAsync();
        first.Should().ContainSingle();

        // Second check within throttle window — should be suppressed
        IReadOnlyList<Alert> second = await svc.CheckForAlertsAsync();
        second.Should().BeEmpty("throttle window hasn't expired");
    }

    [Fact]
    public async Task Throttle_DifferentTypeSameBrandRegion_FiresIndependently()
    {
        InMemoryAlertService svc = CreateService();
        svc.SeedDataPoint("Brand B", "East", "demand_spike", baseline: 1000, current: 1300);
        svc.SeedDataPoint("Brand B", "East", "supply_drop", baseline: 1000, current: 800);

        IReadOnlyList<Alert> alerts = await svc.CheckForAlertsAsync();

        alerts.Should().HaveCount(2);
        alerts.Select(a => a.Type).Should().Contain("demand_spike");
        alerts.Select(a => a.Type).Should().Contain("supply_drop");
    }

    [Fact]
    public async Task Throttle_SameTypeDifferentBrand_FiresIndependently()
    {
        InMemoryAlertService svc = CreateService();
        svc.SeedDataPoint("Brand C", "West", "demand_spike", baseline: 1000, current: 1300);
        svc.SeedDataPoint("Brand D", "West", "demand_spike", baseline: 1000, current: 1400);

        IReadOnlyList<Alert> alerts = await svc.CheckForAlertsAsync();

        alerts.Should().HaveCount(2);
        alerts.Select(a => a.Brand).Should().BeEquivalentTo(["Brand C", "Brand D"]);
    }

    [Fact]
    public async Task Throttle_SameTypeSameBrandDifferentRegion_FiresIndependently()
    {
        InMemoryAlertService svc = CreateService();
        svc.SeedDataPoint("Brand E", "West", "demand_spike", baseline: 1000, current: 1300);
        svc.SeedDataPoint("Brand E", "East", "demand_spike", baseline: 1000, current: 1300);

        IReadOnlyList<Alert> alerts = await svc.CheckForAlertsAsync();

        alerts.Should().HaveCount(2);
        alerts.Select(a => a.Region).Should().BeEquivalentTo(["West", "East"]);
    }

    [Fact]
    public async Task Throttle_AfterWindowExpires_AlertCanFireAgain()
    {
        // Use 1-second throttle window for testing
        InMemoryAlertService svc = CreateService(throttleWindow: TimeSpan.FromMilliseconds(100));
        svc.SeedDataPoint("Brand F", "South", "demand_spike", baseline: 1000, current: 1300);

        IReadOnlyList<Alert> first = await svc.CheckForAlertsAsync();
        first.Should().ContainSingle();

        // Wait for throttle to expire
        await Task.Delay(150);

        IReadOnlyList<Alert> second = await svc.CheckForAlertsAsync();
        second.Should().ContainSingle("throttle window has expired");
    }

    [Fact]
    public async Task Throttle_StatePersistsAcrossCheckCycles()
    {
        InMemoryAlertService svc = CreateService();
        svc.SeedDataPoint("Brand G", "North", "demand_spike", baseline: 1000, current: 1300);

        // First cycle fires
        await svc.CheckForAlertsAsync();
        // Second cycle suppressed
        IReadOnlyList<Alert> second = await svc.CheckForAlertsAsync();
        // Third cycle still suppressed
        IReadOnlyList<Alert> third = await svc.CheckForAlertsAsync();

        second.Should().BeEmpty();
        third.Should().BeEmpty("throttle state persists across check cycles");
    }

    [Fact]
    public async Task IsThrottled_AfterAlert_ReturnsTrue()
    {
        InMemoryAlertService svc = CreateService();
        svc.SeedDataPoint("Brand H", "West", "demand_spike", baseline: 1000, current: 1300);
        await svc.CheckForAlertsAsync();

        svc.IsThrottled("demand_spike", "Brand H", "West").Should().BeTrue();
    }

    [Fact]
    public void IsThrottled_NoAlert_ReturnsFalse()
    {
        InMemoryAlertService svc = CreateService();
        svc.IsThrottled("demand_spike", "Brand I", "East").Should().BeFalse();
    }

    [Fact]
    public async Task ResetThrottle_AllowsAlertToFireAgain()
    {
        InMemoryAlertService svc = CreateService();
        svc.SeedDataPoint("Brand J", "West", "demand_spike", baseline: 1000, current: 1300);
        await svc.CheckForAlertsAsync();

        svc.IsThrottled("demand_spike", "Brand J", "West").Should().BeTrue();
        svc.ResetThrottle("demand_spike", "Brand J", "West");
        svc.IsThrottled("demand_spike", "Brand J", "West").Should().BeFalse();
    }

    [Fact]
    public async Task Throttle_ManualTimestampExpiry_AlertFires()
    {
        InMemoryAlertService svc = CreateService();
        svc.SeedDataPoint("Brand K", "East", "supply_drop", baseline: 1000, current: 800);

        // Fire once
        await svc.CheckForAlertsAsync();

        // Set throttle timestamp to 2 hours ago (expired)
        svc.SetThrottleTimestamp("supply_drop", "Brand K", "East",
            DateTimeOffset.UtcNow.AddHours(-2));

        // Should fire again
        IReadOnlyList<Alert> alerts = await svc.CheckForAlertsAsync();
        alerts.Should().ContainSingle();
    }

    [Fact]
    public async Task Throttle_ThreeTypeSameLocation_AllFireIndependently()
    {
        InMemoryAlertService svc = CreateService();
        svc.SeedDataPoint("Brand L", "Central", "demand_spike", baseline: 1000, current: 1300);
        svc.SeedDataPoint("Brand L", "Central", "supply_drop", baseline: 1000, current: 800);
        svc.SeedDataPoint("Brand L", "Central", "trend_reversal", baseline: 1000, current: 1200);

        IReadOnlyList<Alert> alerts = await svc.CheckForAlertsAsync();

        alerts.Should().HaveCount(3, "each (type, brand, region) is independently throttled");
    }
}

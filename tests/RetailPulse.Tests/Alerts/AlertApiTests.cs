using FluentAssertions;
using RetailPulse.Api.Alerts;
using RetailPulse.Contracts.Alerts;

namespace RetailPulse.Tests.Alerts;

/// <summary>
/// Tests for alert REST API behavior (service-level, not HTTP integration).
/// Covers: GET active, GET history, POST snooze, POST dismiss.
/// 8+ tests.
/// </summary>
public class AlertApiTests
{
    private InMemoryAlertService CreateService()
        => new(throttleWindow: TimeSpan.FromMilliseconds(50));

    private async Task<InMemoryAlertService> SeedAndFireAlerts()
    {
        var svc = CreateService();
        svc.SeedDataPoint("Brand A", "West", "demand_spike", baseline: 1000, current: 1400);
        svc.SeedDataPoint("Brand B", "East", "supply_drop", baseline: 1000, current: 700);
        svc.SeedDataPoint("Brand C", "South", "trend_reversal", baseline: 1000, current: 1200);
        await svc.CheckForAlertsAsync();
        return svc;
    }

    #region GET /api/alerts/active

    [Fact]
    public async Task GetActiveAlerts_ReturnsCurrentlyFiring()
    {
        var svc = await SeedAndFireAlerts();

        var active = await svc.GetActiveAlertsAsync();

        active.Should().HaveCount(3);
        active.Should().OnlyContain(a => !string.IsNullOrEmpty(a.Id));
    }

    [Fact]
    public async Task GetActiveAlerts_ExcludesDismissed()
    {
        var svc = await SeedAndFireAlerts();
        var all = await svc.GetActiveAlertsAsync();
        await svc.DismissAsync(all[0].Id, "user-1");

        var active = await svc.GetActiveAlertsAsync();

        active.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetActiveAlerts_EmptyWhenNoneExist()
    {
        var svc = CreateService();

        var active = await svc.GetActiveAlertsAsync();

        active.Should().BeEmpty();
    }

    #endregion

    #region GET /api/alerts/history

    [Fact]
    public async Task GetHistory_ReturnsChronologicalOrder()
    {
        var svc = await SeedAndFireAlerts();

        var history = await svc.GetHistoryAsync("user-1");

        history.Should().HaveCount(3);
        history.Should().BeInDescendingOrder(a => a.DetectedAt);
    }

    [Fact]
    public async Task GetHistory_RespectsLimit()
    {
        var svc = await SeedAndFireAlerts();

        var history = await svc.GetHistoryAsync("user-1", limit: 2);

        history.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetHistory_EmptyWhenNoAlerts()
    {
        var svc = CreateService();

        var history = await svc.GetHistoryAsync("user-1");

        history.Should().BeEmpty();
    }

    #endregion

    #region POST /api/alerts/{id}/snooze

    [Fact]
    public async Task Snooze_CreatesSnoozeRecord()
    {
        var svc = await SeedAndFireAlerts();

        await svc.SnoozeAsync("demand_spike", "user-1", TimeSpan.FromHours(1));

        var snoozes = svc.GetSnoozes("user-1");
        snoozes.Should().ContainSingle();
        snoozes[0].Type.Should().Be("demand_spike");
    }

    #endregion

    #region POST /api/alerts/{id}/dismiss

    [Fact]
    public async Task Dismiss_MarksAlertDismissed()
    {
        var svc = await SeedAndFireAlerts();
        var alerts = await svc.GetActiveAlertsAsync();
        var alertId = alerts[0].Id;

        await svc.DismissAsync(alertId, "user-1");

        var active = await svc.GetActiveAlertsAsync();
        active.Should().NotContain(a => a.Id == alertId);
    }

    [Fact]
    public async Task Dismiss_IdempotentOnSameAlert()
    {
        var svc = await SeedAndFireAlerts();
        var alerts = await svc.GetActiveAlertsAsync();
        var alertId = alerts[0].Id;

        // Dismiss twice — should not throw
        await svc.DismissAsync(alertId, "user-1");
        var act = () => svc.DismissAsync(alertId, "user-1");

        await act.Should().NotThrowAsync();
    }

    #endregion
}

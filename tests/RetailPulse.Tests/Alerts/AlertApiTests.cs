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
    private static InMemoryAlertService CreateService()
        => new(throttleWindow: TimeSpan.FromMilliseconds(50));

    private async Task<InMemoryAlertService> SeedAndFireAlerts()
    {
        InMemoryAlertService svc = CreateService();
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
        InMemoryAlertService svc = await SeedAndFireAlerts();

        IReadOnlyList<Alert> active = await svc.GetActiveAlertsAsync();

        active.Should().HaveCount(3);
        active.Should().OnlyContain(a => !string.IsNullOrEmpty(a.Id));
    }

    [Fact]
    public async Task GetActiveAlerts_ExcludesDismissed()
    {
        InMemoryAlertService svc = await SeedAndFireAlerts();
        IReadOnlyList<Alert> all = await svc.GetActiveAlertsAsync();
        await svc.DismissAsync(all[0].Id, "user-1");

        IReadOnlyList<Alert> active = await svc.GetActiveAlertsAsync();

        active.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetActiveAlerts_EmptyWhenNoneExist()
    {
        InMemoryAlertService svc = CreateService();

        IReadOnlyList<Alert> active = await svc.GetActiveAlertsAsync();

        active.Should().BeEmpty();
    }

    #endregion

    #region GET /api/alerts/history

    [Fact]
    public async Task GetHistory_ReturnsChronologicalOrder()
    {
        InMemoryAlertService svc = await SeedAndFireAlerts();

        IReadOnlyList<Alert> history = await svc.GetHistoryAsync("user-1");

        history.Should().HaveCount(3);
        history.Should().BeInDescendingOrder(a => a.DetectedAt);
    }

    [Fact]
    public async Task GetHistory_RespectsLimit()
    {
        InMemoryAlertService svc = await SeedAndFireAlerts();

        IReadOnlyList<Alert> history = await svc.GetHistoryAsync("user-1", limit: 2);

        history.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetHistory_EmptyWhenNoAlerts()
    {
        InMemoryAlertService svc = CreateService();

        IReadOnlyList<Alert> history = await svc.GetHistoryAsync("user-1");

        history.Should().BeEmpty();
    }

    #endregion

    #region POST /api/alerts/{id}/snooze

    [Fact]
    public async Task Snooze_CreatesSnoozeRecord()
    {
        InMemoryAlertService svc = await SeedAndFireAlerts();

        await svc.SnoozeAsync("demand_spike", "user-1", TimeSpan.FromHours(1));

        IReadOnlyList<InMemoryAlertService.SnoozeEntry> snoozes = svc.GetSnoozes("user-1");
        snoozes.Should().ContainSingle();
        snoozes[0].Type.Should().Be("demand_spike");
    }

    #endregion

    #region POST /api/alerts/{id}/dismiss

    [Fact]
    public async Task Dismiss_MarksAlertDismissed()
    {
        InMemoryAlertService svc = await SeedAndFireAlerts();
        IReadOnlyList<Alert> alerts = await svc.GetActiveAlertsAsync();
        string alertId = alerts[0].Id;

        await svc.DismissAsync(alertId, "user-1");

        IReadOnlyList<Alert> active = await svc.GetActiveAlertsAsync();
        active.Should().NotContain(a => a.Id == alertId);
    }

    [Fact]
    public async Task Dismiss_IdempotentOnSameAlert()
    {
        InMemoryAlertService svc = await SeedAndFireAlerts();
        IReadOnlyList<Alert> alerts = await svc.GetActiveAlertsAsync();
        string alertId = alerts[0].Id;

        // Dismiss twice — should not throw
        await svc.DismissAsync(alertId, "user-1");
        Func<Task> act = () => svc.DismissAsync(alertId, "user-1");

        await act.Should().NotThrowAsync();
    }

    #endregion
}

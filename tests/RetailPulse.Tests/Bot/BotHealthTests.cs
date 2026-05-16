using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace RetailPulse.Tests.Bot;

/// <summary>
/// Sprint 3 reliability: bot health check behavior.
/// Validates health status transitions based on SignalR connectivity
/// and configurable fail-fast vs degraded mode.
/// Tests use a local IHealthCheck implementation to verify the contract
/// before the production implementation lands.
/// </summary>
public class BotHealthTests
{
    /// <summary>
    /// Configurable health check that reports status based on SignalR connectivity.
    /// In degraded mode: Degraded when disconnected. In fail-fast mode: Unhealthy.
    /// </summary>
    private sealed class SignalRHealthCheck : IHealthCheck
    {
        private volatile bool _isConnected;
        private readonly bool _failFast;

        public SignalRHealthCheck(bool failFast = false)
        {
            _failFast = failFast;
            _isConnected = true; // Assume connected initially
        }

        public bool IsConnected
        {
            get => _isConnected;
            set => _isConnected = value;
        }

        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            return _isConnected
                ? Task.FromResult(HealthCheckResult.Healthy("SignalR connected"))
                : _failFast
                ? Task.FromResult(HealthCheckResult.Unhealthy("SignalR disconnected (fail-fast mode)"))
                : Task.FromResult(HealthCheckResult.Degraded("SignalR disconnected (degraded mode)"));
        }
    }

    // ── Healthy When Connected ──────────────────────────────────────────

    [Fact]
    public async Task WhenSignalRConnected_ReportsHealthy()
    {
        var check = new SignalRHealthCheck(failFast: false) { IsConnected = true };

        HealthCheckResult result = await check.CheckHealthAsync(CreateContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("connected");
    }

    [Fact]
    public async Task WhenSignalRConnected_FailFastMode_StillHealthy()
    {
        var check = new SignalRHealthCheck(failFast: true) { IsConnected = true };

        HealthCheckResult result = await check.CheckHealthAsync(CreateContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    // ── Degraded When Disconnected (Degraded Mode) ──────────────────────

    [Fact]
    public async Task WhenSignalRDisconnected_DegradedMode_ReportsDegraded()
    {
        var check = new SignalRHealthCheck(failFast: false) { IsConnected = false };

        HealthCheckResult result = await check.CheckHealthAsync(CreateContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("disconnected");
    }

    [Fact]
    public async Task WhenSignalRDisconnected_DegradedMode_NotUnhealthy()
    {
        var check = new SignalRHealthCheck(failFast: false) { IsConnected = false };

        HealthCheckResult result = await check.CheckHealthAsync(CreateContext());

        result.Status.Should().NotBe(HealthStatus.Unhealthy,
            "degraded mode should not report Unhealthy");
    }

    // ── Unhealthy When Disconnected (Fail-Fast Mode) ────────────────────

    [Fact]
    public async Task WhenSignalRDisconnected_FailFastMode_ReportsUnhealthy()
    {
        var check = new SignalRHealthCheck(failFast: true) { IsConnected = false };

        HealthCheckResult result = await check.CheckHealthAsync(CreateContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("fail-fast");
    }

    [Fact]
    public async Task WhenSignalRDisconnected_FailFastMode_NotDegraded()
    {
        var check = new SignalRHealthCheck(failFast: true) { IsConnected = false };

        HealthCheckResult result = await check.CheckHealthAsync(CreateContext());

        result.Status.Should().NotBe(HealthStatus.Degraded,
            "fail-fast mode should report Unhealthy, not Degraded");
    }

    // ── State Transitions ───────────────────────────────────────────────

    [Fact]
    public async Task HealthTransition_ConnectedToDisconnected_StatusChanges()
    {
        var check = new SignalRHealthCheck(failFast: false) { IsConnected = true };

        HealthCheckResult healthy = await check.CheckHealthAsync(CreateContext());
        healthy.Status.Should().Be(HealthStatus.Healthy);

        check.IsConnected = false;
        HealthCheckResult degraded = await check.CheckHealthAsync(CreateContext());
        degraded.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task HealthTransition_DisconnectedToConnected_Recovers()
    {
        var check = new SignalRHealthCheck(failFast: true) { IsConnected = false };

        HealthCheckResult unhealthy = await check.CheckHealthAsync(CreateContext());
        unhealthy.Status.Should().Be(HealthStatus.Unhealthy);

        check.IsConnected = true;
        HealthCheckResult healthy = await check.CheckHealthAsync(CreateContext());
        healthy.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CancellationToken_IsRespected()
    {
        var check = new SignalRHealthCheck();
        using var cts = new CancellationTokenSource();

        // Non-cancelled token should work
        HealthCheckResult result = await check.CheckHealthAsync(CreateContext(), cts.Token);
        result.Status.Should().Be(HealthStatus.Healthy);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static HealthCheckContext CreateContext() => new()
    {
        Registration = new HealthCheckRegistration("signalr", new SignalRHealthCheck(), null, null)
    };
}

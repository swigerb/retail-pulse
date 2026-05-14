using Microsoft.Extensions.Diagnostics.HealthChecks;
using RetailPulse.TeamsBot.Services;

namespace RetailPulse.TeamsBot;

/// <summary>
/// Health check that reports the SignalR telemetry connection status.
/// Mode is configurable: "fail-fast" returns Unhealthy, "degraded" returns Degraded.
/// </summary>
internal sealed class SignalRHealthCheck : IHealthCheck
{
    private readonly TelemetrySignalRClient _client;
    private readonly string _healthMode;

    public SignalRHealthCheck(TelemetrySignalRClient client, IConfiguration configuration)
    {
        _client = client;
        _healthMode = configuration["TeamsBot:HealthMode"] ?? "degraded";
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_client.IsConnected)
            return Task.FromResult(HealthCheckResult.Healthy("SignalR connected"));

        if (_healthMode == "fail-fast")
            return Task.FromResult(HealthCheckResult.Unhealthy("SignalR disconnected (fail-fast mode)"));

        return Task.FromResult(HealthCheckResult.Degraded("SignalR disconnected (degraded mode)"));
    }
}

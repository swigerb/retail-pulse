using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RetailPulse.Api.Resilience;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// A8 — Content Safety breaker state is reported on the same
/// <see cref="CircuitBreakerHealthCheck"/> data surface as the MCP breaker so
/// operators see remote-safety outages on the existing readiness probe.
/// </summary>
public class ContentSafetyHealthCheckTests
{
    [Fact]
    public async Task ReportContentSafetyState_SurfacesInHealthData()
    {
        CircuitBreakerHealthCheck.ReportContentSafetyState(CircuitBreakerState.Open);
        try
        {
            var check = new CircuitBreakerHealthCheck();
            HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

            result.Data.Should().ContainKey("contentSafetyCircuitState");
            result.Data["contentSafetyCircuitState"].Should().Be(CircuitBreakerState.Open.ToString());
        }
        finally
        {
            CircuitBreakerHealthCheck.ReportContentSafetyState(CircuitBreakerState.Closed);
        }
    }
}

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Polly.CircuitBreaker;

namespace RetailPulse.Api.Resilience;

/// <summary>
/// Health check that reports the state of the MCP circuit breaker.
/// </summary>
public class CircuitBreakerHealthCheck : IHealthCheck
{
    private static CircuitBreakerState _state = CircuitBreakerState.Closed;
    private static DateTimeOffset _lastStateChange = DateTimeOffset.UtcNow;

    public static void ReportState(CircuitBreakerState state)
    {
        if (_state != state)
        {
            _state = state;
            _lastStateChange = DateTimeOffset.UtcNow;
        }
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var data = new Dictionary<string, object>
        {
            ["circuitState"] = _state.ToString(),
            ["lastStateChange"] = _lastStateChange.ToString("O")
        };

        HealthCheckResult result = _state switch
        {
            CircuitBreakerState.Closed => HealthCheckResult.Healthy("Circuit breaker is closed (healthy).", data),
            CircuitBreakerState.HalfOpen => HealthCheckResult.Degraded("Circuit breaker is half-open (testing recovery).", data: data),
            CircuitBreakerState.Open => HealthCheckResult.Unhealthy("Circuit breaker is open (MCP server unavailable).", data: data),
            _ => HealthCheckResult.Healthy("Circuit breaker state unknown.", data)
        };

        return Task.FromResult(result);
    }
}

/// <summary>
/// Enumeration of circuit breaker states for health reporting.
/// </summary>
public enum CircuitBreakerState
{
    Closed,
    Open,
    HalfOpen
}

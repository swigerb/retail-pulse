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
    private static CircuitBreakerState _contentSafetyState = CircuitBreakerState.Closed;
    private static DateTimeOffset _contentSafetyLastChange = DateTimeOffset.UtcNow;

    public static void ReportState(CircuitBreakerState state)
    {
        if (_state != state)
        {
            _state = state;
            _lastStateChange = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Reports the Content Safety breaker state alongside the MCP breaker so a
    /// remote safety outage is visible on the same health probe. The state is
    /// exposed under the <c>contentSafetyCircuitState</c> data key and never
    /// escalates the overall health check status (fail policy is set at the
    /// evaluator).
    /// </summary>
    public static void ReportContentSafetyState(CircuitBreakerState state)
    {
        if (_contentSafetyState != state)
        {
            _contentSafetyState = state;
            _contentSafetyLastChange = DateTimeOffset.UtcNow;
        }
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var data = new Dictionary<string, object>
        {
            ["circuitState"] = _state.ToString(),
            ["lastStateChange"] = _lastStateChange.ToString("O"),
            ["contentSafetyCircuitState"] = _contentSafetyState.ToString(),
            ["contentSafetyLastStateChange"] = _contentSafetyLastChange.ToString("O")
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

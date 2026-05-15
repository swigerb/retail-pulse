using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace RetailPulse.Api.Resilience;

/// <summary>
/// Extension methods to wire up resilience policies (retry + circuit breaker)
/// on the MCP HttpClient.
/// </summary>
public static class ResilienceExtensions
{
    /// <summary>
    /// Adds retry (3 attempts, exponential backoff) and circuit breaker (5 failures / 30s → open 30s)
    /// to an <see cref="IHttpClientBuilder"/>. Retry is composed inside the circuit breaker
    /// so that a single logical request can retry within the breaker's protection.
    /// </summary>
    public static IHttpClientBuilder AddMcpResilienceHandler(this IHttpClientBuilder builder)
    {
        builder.AddResilienceHandler("McpResilience", pipelineBuilder =>
        {
            // Circuit breaker (outermost) — opens after 5 failures within 30s sampling window
            pipelineBuilder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 5,
                BreakDuration = TimeSpan.FromSeconds(30),
                OnOpened = args =>
                {
                    CircuitBreakerHealthCheck.ReportState(CircuitBreakerState.Open);
                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    CircuitBreakerHealthCheck.ReportState(CircuitBreakerState.Closed);
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = args =>
                {
                    CircuitBreakerHealthCheck.ReportState(CircuitBreakerState.HalfOpen);
                    return ValueTask.CompletedTask;
                }
            });

            // Retry (innermost) — 3 attempts with exponential backoff starting at 1s
            pipelineBuilder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true
            });
        });

        return builder;
    }
}

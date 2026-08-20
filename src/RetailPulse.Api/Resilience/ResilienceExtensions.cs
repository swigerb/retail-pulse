using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Timeout;

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

    /// <summary>
    /// Adds a bounded timeout + circuit breaker to the Content Safety
    /// <see cref="HttpClient"/>. Mirrors the MCP breaker semantics (5 failures /
    /// 30 s sampling, 30 s open) so a Content Safety outage cannot cascade back
    /// into the request loop, and feeds breaker state into
    /// <see cref="CircuitBreakerHealthCheck"/> under the <c>contentsafety</c>
    /// key. No retries: the caller applies its fail-open / fail-closed policy on
    /// the first failure so a slow Content Safety region does not multiply the
    /// per-call latency budget by 3x.
    /// </summary>
    public static IHttpClientBuilder AddContentSafetyResilienceHandler(
        this IHttpClientBuilder builder,
        int timeoutMs)
    {
        int boundedTimeoutMs = Math.Max(200, timeoutMs);
        builder.AddResilienceHandler("ContentSafetyResilience", pipelineBuilder =>
        {
            pipelineBuilder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 5,
                BreakDuration = TimeSpan.FromSeconds(30),
                OnOpened = args =>
                {
                    CircuitBreakerHealthCheck.ReportContentSafetyState(CircuitBreakerState.Open);
                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    CircuitBreakerHealthCheck.ReportContentSafetyState(CircuitBreakerState.Closed);
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = args =>
                {
                    CircuitBreakerHealthCheck.ReportContentSafetyState(CircuitBreakerState.HalfOpen);
                    return ValueTask.CompletedTask;
                }
            });

            pipelineBuilder.AddTimeout(new HttpTimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromMilliseconds(boundedTimeoutMs),
            });
        });

        return builder;
    }
}

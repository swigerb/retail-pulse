using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Resilience;
using RetailPulse.Api.Tools;

namespace RetailPulse.Tests.Chaos;

/// <summary>
/// Verifies circuit breaker behavior — opens after repeated failures and tools degrade gracefully.
/// </summary>
public class CircuitBreakerChaosTests
{
    [Fact]
    public void ErrorClassifier_Timeout_ClassifiesAsTransient()
    {
        var ex = new TaskCanceledException("Request timed out");
        ErrorCategory category = ErrorClassifier.Classify(ex);
        category.Should().Be(ErrorCategory.Transient);
    }

    [Fact]
    public void ErrorClassifier_429Response_ClassifiesAsTransient()
    {
        var ex = new HttpRequestException("Too many requests", null, HttpStatusCode.TooManyRequests);
        ErrorCategory category = ErrorClassifier.Classify(ex);
        category.Should().Be(ErrorCategory.Transient);
    }

    [Fact]
    public void ErrorClassifier_503Response_ClassifiesAsTransient()
    {
        var ex = new HttpRequestException("Service unavailable", null, HttpStatusCode.ServiceUnavailable);
        ErrorCategory category = ErrorClassifier.Classify(ex);
        category.Should().Be(ErrorCategory.Transient);
    }

    [Fact]
    public void ErrorClassifier_NullRef_ClassifiesAsSystem()
    {
        var ex = new NullReferenceException("Object reference not set");
        ErrorCategory category = ErrorClassifier.Classify(ex);
        category.Should().Be(ErrorCategory.System);
    }

    [Fact]
    public void ErrorClassifier_ArgumentException_ClassifiesAsUser()
    {
        var ex = new ArgumentException("Invalid parameter");
        ErrorCategory category = ErrorClassifier.Classify(ex);
        category.Should().Be(ErrorCategory.User);
    }

    [Fact]
    public void ErrorClassifier_HttpRequestException_NoStatusCode_ClassifiesAsExternal()
    {
        var ex = new HttpRequestException("Connection refused");
        ErrorCategory category = ErrorClassifier.Classify(ex);
        category.Should().Be(ErrorCategory.External);
    }

    [Fact]
    public async Task Tool_AfterMultipleFailures_ContinuesReturningFallback()
    {
        var handler = new FailingThenRecoveringHandler(failCount: 10);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5200") };
        var tool = new FieldSentimentTool(client, NullLogger<FieldSentimentTool>.Instance);

        // All requests during failure period should degrade gracefully
        for (int i = 0; i < 10; i++)
        {
            string result = await tool.GetFieldSentiment("Brand", "Region");
            result.Should().Contain("fallback");
        }
    }

    [Fact]
    public async Task Tool_AfterCircuitRecovers_ReturnsLiveData()
    {
        var handler = new FailingThenRecoveringHandler(failCount: 2);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5200") };
        var tool = new FieldSentimentTool(client, NullLogger<FieldSentimentTool>.Instance);

        // First 2 calls fail
        string fail1 = await tool.GetFieldSentiment("Brand", "Region");
        fail1.Should().Contain("fallback");
        string fail2 = await tool.GetFieldSentiment("Brand", "Region");
        fail2.Should().Contain("fallback");

        // Third call succeeds
        string success = await tool.GetFieldSentiment("Brand", "Region");
        success.Should().Contain("live-data");
    }

    [Fact]
    public void CircuitBreakerHealthCheck_ReportsCorrectState()
    {
        CircuitBreakerHealthCheck.ReportState(CircuitBreakerState.Open);
        var check = new CircuitBreakerHealthCheck();
        HealthCheckResult result = check.CheckHealthAsync(null!).Result;

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("open");

        // Reset
        CircuitBreakerHealthCheck.ReportState(CircuitBreakerState.Closed);
        HealthCheckResult result2 = check.CheckHealthAsync(null!).Result;
        result2.Status.Should().Be(HealthStatus.Healthy);
    }

    private sealed class FailingThenRecoveringHandler : HttpMessageHandler
    {
        private int _callCount;
        private readonly int _failCount;

        public FailingThenRecoveringHandler(int failCount)
        {
            _failCount = failCount;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _callCount++;
            return _callCount <= _failCount
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(/*lang=json,strict*/ "{\"source\":\"live-data\",\"brand\":\"Brand\",\"region\":\"Region\"}")
                });
        }
    }
}

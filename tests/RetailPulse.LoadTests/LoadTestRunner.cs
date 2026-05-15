using FluentAssertions;
using NBomber.CSharp;

namespace RetailPulse.LoadTests;

/// <summary>
/// xUnit wrapper that runs NBomber scenarios and asserts SLA thresholds.
/// These tests are meant to be run separately (not in regular CI)
/// against a running instance of the application.
/// </summary>
/// <remarks>
/// To run: start the API first, then execute:
///   dotnet test tests/RetailPulse.LoadTests --filter "Category=LoadTest"
/// Or use the run-load-tests.ps1 script.
/// </remarks>
[Trait("Category", "LoadTest")]
public class LoadTestRunner
{
    [Fact(Skip = "Load tests require a running API instance — run via run-load-tests.ps1")]
    public void ChatEndpoint_MeetsLatencySla()
    {
        var scenario = ChatEndpointScenario.Create();

        var stats = NBomberRunner
            .RegisterScenarios(scenario)
            .WithReportFolder("./load-test-reports")
            .Run();

        var scenarioStats = stats.ScenarioStats[0];

        // p95 < 5 seconds for chat endpoint
        scenarioStats.Ok.Latency.Percent95.Should().BeLessThan(5000,
            "Chat endpoint p95 latency must be under 5 seconds");

        // Success rate > 95%
        var successRate = (double)scenarioStats.Ok.Request.Count /
            (scenarioStats.Ok.Request.Count + scenarioStats.Fail.Request.Count) * 100;
        successRate.Should().BeGreaterThan(95, "Chat endpoint success rate must exceed 95%");
    }

    [Fact(Skip = "Load tests require a running API instance — run via run-load-tests.ps1")]
    public void HealthCheck_MeetsLatencySla()
    {
        var scenario = HealthCheckScenario.Create();

        var stats = NBomberRunner
            .RegisterScenarios(scenario)
            .WithReportFolder("./load-test-reports")
            .Run();

        var scenarioStats = stats.ScenarioStats[0];

        // p99 < 200ms for health check
        scenarioStats.Ok.Latency.Percent99.Should().BeLessThan(200,
            "Health check p99 latency must be under 200ms");

        // Success rate > 99.9%
        var successRate = (double)scenarioStats.Ok.Request.Count /
            (scenarioStats.Ok.Request.Count + scenarioStats.Fail.Request.Count) * 100;
        successRate.Should().BeGreaterThan(99.9, "Health check success rate must exceed 99.9%");
    }
}

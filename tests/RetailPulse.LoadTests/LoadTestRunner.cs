using FluentAssertions;
using NBomber.Contracts;
using NBomber.Contracts.Stats;
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
        ScenarioProps scenario = ChatEndpointScenario.Create();

        NodeStats stats = NBomberRunner
            .RegisterScenarios(scenario)
            .WithReportFolder("./load-test-reports")
            .Run();

        ScenarioStats scenarioStats = stats.ScenarioStats[0];

        // p95 < 5 seconds for chat endpoint
        scenarioStats.Ok.Latency.Percent95.Should().BeLessThan(5000,
            "Chat endpoint p95 latency must be under 5 seconds");

        // Success rate > 95%
        double successRate = (double)scenarioStats.Ok.Request.Count /
            (scenarioStats.Ok.Request.Count + scenarioStats.Fail.Request.Count) * 100;
        successRate.Should().BeGreaterThan(95, "Chat endpoint success rate must exceed 95%");
    }

    [Fact(Skip = "Load tests require a running API instance — run via run-load-tests.ps1")]
    public void HealthCheck_MeetsLatencySla()
    {
        ScenarioProps scenario = HealthCheckScenario.Create();

        NodeStats stats = NBomberRunner
            .RegisterScenarios(scenario)
            .WithReportFolder("./load-test-reports")
            .Run();

        ScenarioStats scenarioStats = stats.ScenarioStats[0];

        // p99 < 200ms for health check
        scenarioStats.Ok.Latency.Percent99.Should().BeLessThan(200,
            "Health check p99 latency must be under 200ms");

        // Success rate > 99.9%
        double successRate = (double)scenarioStats.Ok.Request.Count /
            (scenarioStats.Ok.Request.Count + scenarioStats.Fail.Request.Count) * 100;
        successRate.Should().BeGreaterThan(99.9, "Health check success rate must exceed 99.9%");
    }

    /// <summary>
    /// Wave 2 QA sweep (#97) SLA guard for the plan-first path. Plans are
    /// strictly more expensive than the fast path (planner + executor +
    /// synthesis) so the p95 budget is doubled while success-rate remains at
    /// the same 95% floor. The scenario is deterministic (fixed multi-domain
    /// prompt + <c>forceExecutionPath=plan</c>) so a repeat run establishes
    /// a comparable baseline across releases.
    /// </summary>
    [Fact(Skip = "Load tests require a running API instance — run via run-load-tests.ps1")]
    public void PlanPathChatEndpoint_MeetsLatencySla()
    {
        ScenarioProps scenario = PlanPathChatEndpointScenario.Create();

        NodeStats stats = NBomberRunner
            .RegisterScenarios(scenario)
            .WithReportFolder("./load-test-reports")
            .Run();

        ScenarioStats scenarioStats = stats.ScenarioStats[0];

        // p95 < 10 seconds for the plan path — twice the fast-path budget to
        // account for planner + executor + synthesis.
        scenarioStats.Ok.Latency.Percent95.Should().BeLessThan(10_000,
            "Plan-path chat endpoint p95 latency must be under 10 seconds.");

        double successRate = (double)scenarioStats.Ok.Request.Count /
            (scenarioStats.Ok.Request.Count + scenarioStats.Fail.Request.Count) * 100;
        successRate.Should().BeGreaterThan(95, "Plan-path chat endpoint success rate must exceed 95%.");
    }
}

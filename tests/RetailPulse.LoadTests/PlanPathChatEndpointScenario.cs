using System.Text;
using System.Text.Json;
using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace RetailPulse.LoadTests;

/// <summary>
/// Wave 2 QA sweep (#97) load scenario for the plan-first branch of
/// <c>/api/chat</c>. Multi-domain payloads plus
/// <c>forceExecutionPath=&quot;plan&quot;</c> guarantee <c>HybridExecutionDecider</c>
/// picks Plan, so this scenario exercises the full planner → executor →
/// synthesis pipeline (single specialist run + coherent reply). The
/// ramping profile is intentionally lower than the fast-path scenario:
/// plans are strictly more expensive, so 5 rps × 60 s is enough signal
/// while keeping runs bounded when a reviewer runs it against a local
/// endpoint. Success-rate and p95 thresholds apply against the reply
/// itself; the runner asserts them against
/// <see cref="LoadTestRunner.PlanPathChatEndpoint_MeetsLatencySla"/>.
/// </summary>
public class PlanPathChatEndpointScenario
{
    private const string BaseUrl = "http://localhost:5000";

    public static ScenarioProps Create()
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri(BaseUrl) };

        string chatPayload = JsonSerializer.Serialize(new
        {
            // Cross-domain question: touches demand-forecasting AND
            // supply-shipments so the hybrid decider routes to Plan even
            // without the forceExecutionPath override. The override is set
            // so the scenario stays deterministic across router tunings.
            message = "Compare Q4 demand for Apex Grill in the Southwest with current inventory health and outstanding shipments.",
            sessionId = $"loadtest-plan-{Guid.NewGuid():N}",
            forceExecutionPath = "plan",
        });

        return Scenario.Create("plan_path_chat_endpoint", async context =>
            {
                HttpRequestMessage request = Http.CreateRequest("POST", $"{BaseUrl}/api/chat")
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(new StringContent(chatPayload, Encoding.UTF8, "application/json"));

                Response<HttpResponseMessage> response = await Http.Send(httpClient, request);
                return response;
            })
            .WithoutWarmUp()
            .WithLoadSimulations(
                Simulation.RampingInject(rate: 5, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(60))
            );
    }
}

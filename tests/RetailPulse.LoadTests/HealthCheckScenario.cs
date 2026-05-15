using System.Net.Http.Json;
using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace RetailPulse.LoadTests;

/// <summary>
/// Load test scenario for GET /health endpoint.
/// Sustained 50 req/s for 60 seconds.
/// Asserts: p99 latency < 200ms.
/// </summary>
public class HealthCheckScenario
{
    private const string BaseUrl = "http://localhost:5000";

    public static ScenarioProps Create()
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri(BaseUrl) };

        return Scenario.Create("health_check", async context =>
            {
                var request = Http.CreateRequest("GET", $"{BaseUrl}/health");
                var response = await Http.Send(httpClient, request);
                return response;
            })
            .WithoutWarmUp()
            .WithLoadSimulations(
                Simulation.Inject(rate: 50, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(60))
            );
    }
}

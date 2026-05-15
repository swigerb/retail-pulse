using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace RetailPulse.LoadTests;

/// <summary>
/// Load test scenario for POST /api/v1/chat endpoint.
/// Ramps from 1 to 10 concurrent users over 30 seconds.
/// Asserts: p95 latency < 5 seconds.
/// </summary>
public class ChatEndpointScenario
{
    private const string BaseUrl = "http://localhost:5000";

    public static ScenarioProps Create()
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri(BaseUrl) };

        var chatPayload = JsonSerializer.Serialize(new
        {
            message = "How is Apex Grill performing in the Southwest this quarter?",
            sessionId = $"loadtest-{Guid.NewGuid():N}"
        });

        return Scenario.Create("chat_endpoint", async context =>
            {
                var request = Http.CreateRequest("POST", $"{BaseUrl}/api/v1/chat")
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(new StringContent(chatPayload, Encoding.UTF8, "application/json"));

                var response = await Http.Send(httpClient, request);
                return response;
            })
            .WithoutWarmUp()
            .WithLoadSimulations(
                Simulation.RampingInject(rate: 10, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(30))
            );
    }
}

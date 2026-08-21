using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Agents.Planning;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Persistence;

namespace RetailPulse.Tests.Endpoints;

/// <summary>
/// HTTP-shape tests for the plan-first branch of <c>/api/chat</c> (#94 B3).
/// The endpoint MUST:
/// <list type="bullet">
///   <item>Return <c>202 Accepted</c> with plan/review identifiers when the
///     orchestrator suspends (review is enabled and the plan is waiting).</item>
///   <item>Return the pre-#94 <c>200 OK</c> shape when the orchestrator
///     returns a terminal <see cref="PlanOrchestrationResult"/>.</item>
/// </list>
/// The full production endpoint requires Azure credentials at startup, so we
/// mirror only the plan-first branch inline here — the same pattern
/// <see cref="ChatEndpointErrorTests"/> uses to exercise error paths.
/// </summary>
public sealed class PlanReviewChatEndpointTests : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;
    private PlanOrchestrationResult _next = null!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public PlanReviewChatEndpointTests()
    {
        IHostBuilder builder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
                    services.AddSingleton(this);
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                        // Mirror only the suspension-vs-terminal decision the
                        // production endpoint makes for the plan-first branch.
                        endpoints.MapPost("/api/chat",
                            (HttpContext http, ChatRequest _, PlanReviewChatEndpointTests self) =>
                        {
                            PlanOrchestrationResult r = self._next;
                            return r.IsSuspended
                                ? Results.Accepted(
                                    uri: $"/api/plans/{r.PlanId}",
                                    value: new
                                    {
                                        planId = r.PlanId,
                                        status = r.Status,
                                        reviewRequestId = r.ReviewRequestId,
                                        round = r.ReviewRoundNumber,
                                    })
                                : Results.Ok(new ChatResponse(
                                Reply: r.Reply,
                                SessionId: "s",
                                Spans: [],
                                Charts: null,
                                TotalDurationMs: r.DurationMs,
                                Routing: null));
                        }));
                });
            });

        _host = builder.Start();
        _client = _host.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task Suspended_result_returns_202_with_plan_and_review_identifiers()
    {
        _next = PlanOrchestrationResult.Suspended(
            "plan-XYZ", PlanStatus.AwaitingReview,
            "req-777", roundNumber: 0,
            inputTokens: 0, outputTokens: 0, totalTokens: 0);

        HttpResponseMessage resp = await _client.PostAsJsonAsync("/api/chat",
            new ChatRequest("multi-domain question", SessionId: "s"));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("planId").GetString().Should().Be("plan-XYZ");
        doc.RootElement.GetProperty("reviewRequestId").GetString().Should().Be("req-777");
        doc.RootElement.GetProperty("status").GetString().Should().Be(PlanStatus.AwaitingReview);
    }

    [Fact]
    public async Task Terminal_result_returns_200_with_pre_review_chat_shape()
    {
        // Disabled-review path — the orchestrator returned a Completed
        // result. The endpoint MUST NOT return 202; existing clients rely on
        // the 200 ChatResponse shape.
        _next = new PlanOrchestrationResult(
            PlanId: "plan-XYZ",
            Status: PlanStatus.Completed,
            Reply: "the answer",
            DurationMs: 42,
            InputTokens: 5, OutputTokens: 6, TotalTokens: 11,
            Steps: [],
            FailureReason: null);

        HttpResponseMessage resp = await _client.PostAsJsonAsync("/api/chat",
            new ChatRequest("q", SessionId: "s"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        ChatResponse? body = await resp.Content.ReadFromJsonAsync<ChatResponse>(JsonOptions);
        body!.Reply.Should().Be("the answer");
    }
}

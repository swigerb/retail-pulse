using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RetailPulse.Api.Endpoints;
using RetailPulse.Api.Models;
using RetailPulse.Api.Scorecard;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Tests.Scorecard;

public sealed class ScorecardAvailabilityTests
{
    private const string Valid = """{"score":8,"assessment":"Sell-through increased in reported accounts."}""";
    private static readonly ScorecardDimensionConfig[] Dimensions =
    [
        new("Demand Momentum", 0.6, "demand-forecasting"),
        new("Supply Reliability", 0.4, "supply-chain")
    ];

    [Theory]
    [InlineData("success")]
    [InlineData("empty")]
    [InlineData("failure")]
    public async Task Complete_assessments_synthesize_or_report_summary_unavailability(string outcome)
    {
        var chat = new Mock<IChatClient>();
        var call = chat.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()));
        if (outcome == "failure")
            call.ThrowsAsync(new InvalidOperationException("offline synthesis failure"));
        else
            call.ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant, outcome == "empty" ? "" : "Grounded portfolio summary.")));
        var service = Service([Agent("demand-forecasting", Valid).Object, Agent("supply-chain", Valid).Object], chat);
        var result = await service.GenerateAsync([Guid.NewGuid().ToString()]);
        Assert.Equal("complete", Assert.Single(result.Brands).Status);
        Assert.Equal(outcome == "success" ? "Grounded portfolio summary."
            : "Executive summary unavailable. See individual brand assessments.", result.ExecutiveSummary);
        chat.Verify(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("""{"score":6.25,"assessment":"Reported sell-through increased."}""")]
    [InlineData("```json\n{\"score\":6.25,\"assessment\":\"Reported sell-through increased.\"}\n```")]
    public async Task Valid_JSON_and_fenced_JSON_preserve_scores(string reply)
    {
        var service = Service([Agent("demand-forecasting", reply).Object, Agent("supply-chain", reply).Object]);
        var result = await service.GenerateAsync([Guid.NewGuid().ToString()], includeSummary: false);
        var score = Assert.Single(result.Brands);
        Assert.Equal("complete", score.Status);
        Assert.Equal(6.25, score.Dimensions["Demand Momentum"].Score);
        Assert.Equal(6.2, score.OverallScore);
    }

    [Fact]
    public async Task Complete_scores_preserve_weighted_composite_and_are_cached()
    {
        var agent = Agent("demand-forecasting", Valid);
        var supply = Agent("supply-chain", """{"score":3,"assessment":"Reported shipments fell."}""");
        var service = Service([agent.Object, supply.Object]);
        string brand = Guid.NewGuid().ToString();

        var result = await service.GenerateAsync([brand], includeSummary: false);
        var score = Assert.Single(result.Brands);
        Assert.Equal("complete", score.Status);
        Assert.Equal(6.0, score.OverallScore);
        Assert.Equal(4.8, score.Dimensions["Demand Momentum"].WeightedScore!.Value, 6);
        Assert.Equal("available", score.Dimensions["Demand Momentum"].Status);
        Assert.Single(score.ActionItems);
        await service.GenerateAsync([brand], includeSummary: false);
        agent.Verify(a => a.HandleAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{invalid}")]
    [InlineData("{}")]
    [InlineData("""{"score":8}""")]
    [InlineData("""{"assessment":"Reported increase."}""")]
    [InlineData("""{"score":"8","assessment":"Reported increase."}""")]
    [InlineData("""{"score":null,"assessment":"Reported increase."}""")]
    [InlineData("""{"score":0,"assessment":"Reported increase."}""")]
    [InlineData("""{"score":11,"assessment":"Reported increase."}""")]
    [InlineData("""{"score":1e999,"assessment":"Reported increase."}""")]
    [InlineData("""{"score":8,"assessment":7}""")]
    [InlineData("""{"score":8,"assessment":" "}""")]
    [InlineData("""{"score":5,"assessment":"No grounded specialist assessment was returned for this dimension."}""")]
    public async Task Malformed_or_missing_assessments_are_unavailable_not_neutral(string reply)
    {
        var agent = Agent("demand-forecasting", reply);
        var chat = new Mock<IChatClient>(MockBehavior.Strict);
        var service = Service([agent.Object], chat);
        string brand = Guid.NewGuid().ToString();

        var result = await service.GenerateAsync([brand]);
        var score = Assert.Single(result.Brands);
        Assert.Equal("unavailable", score.Status);
        Assert.Null(score.OverallScore);
        Assert.All(score.Dimensions.Values, d =>
        {
            Assert.Null(d.Score);
            Assert.Null(d.WeightedScore);
            Assert.Equal("unavailable", d.Status);
        });
        Assert.Empty(score.ActionItems);
        Assert.Empty(result.TopActions);
        Assert.Contains("incomplete", result.ExecutiveSummary);
        Assert.DoesNotContain("mixed performance", score.Summary);
        Assert.Empty(chat.Invocations);

        await service.GenerateAsync([brand], includeSummary: false);
        agent.Verify(a => a.HandleAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("failed")]
    [InlineData("timeout")]
    public async Task Partial_assessment_preserves_valid_dimensions_without_composite_or_synthesis(string failure)
    {
        var demand = Agent("demand-forecasting", Valid);
        var supply = Agent("supply-chain", Valid);
        supply.Setup(a => a.HandleAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure == "timeout" ? new OperationCanceledException() : new InvalidOperationException("offline failure"));
        var chat = new Mock<IChatClient>(MockBehavior.Strict);
        var service = Service(failure == "missing" ? [demand.Object] : [demand.Object, supply.Object], chat);
        string brand = Guid.NewGuid().ToString();

        var result = await service.GenerateAsync([brand]);
        var score = Assert.Single(result.Brands);
        Assert.Equal("partial", score.Status);
        Assert.Null(score.OverallScore);
        Assert.Equal(8.0, score.Dimensions["Demand Momentum"].Score);
        Assert.Equal(0.6, score.Dimensions["Demand Momentum"].Weight);
        Assert.Null(score.Dimensions["Supply Reliability"].Score);
        Assert.Contains(failure == "timeout" ? "timed out" : failure == "missing" ? "unavailable" : "failed",
            score.Dimensions["Supply Reliability"].Assessment);
        Assert.Empty(chat.Invocations);
        await service.GenerateAsync([brand], includeSummary: false);
        demand.Verify(a => a.HandleAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_converted_to_an_assessment()
    {
        using var cts = new CancellationTokenSource();
        var demand = Agent("demand-forecasting", Valid);
        demand.Setup(a => a.HandleAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .Returns<ChatRequest, CancellationToken>((_, ct) =>
            {
                cts.Cancel();
                return Task.FromCanceled<ChatResponse>(ct);
            });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service([demand.Object]).GenerateAsync([Guid.NewGuid().ToString()], ct: cts.Token));
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("partial")]
    [InlineData("unavailable")]
    public async Task Actual_endpoint_serializes_availability_and_nulls(string state)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(o => o.AddPolicy("strict", _ => RateLimitPartition.GetNoLimiter("test")));
        ISpecialistAgent[] agents = state switch
        {
            "complete" => [Agent("demand-forecasting", Valid).Object, Agent("supply-chain", Valid).Object],
            "partial" => [Agent("demand-forecasting", Valid).Object],
            _ => [Agent("demand-forecasting", "unparseable").Object]
        };
        builder.Services.AddSingleton(Service(agents));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapScorecardEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/scorecard",
            new { brands = new[] { Guid.NewGuid().ToString() }, includeSummary = false });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var brand = json.RootElement.GetProperty("brands")[0];
        Assert.Equal(state, brand.GetProperty("status").GetString());
        Assert.Equal(state == "complete" ? JsonValueKind.Number : JsonValueKind.Null,
            brand.GetProperty("overallScore").ValueKind);
        var supply = brand.GetProperty("dimensions").GetProperty("Supply Reliability");
        Assert.Equal(state == "complete" ? "available" : "unavailable", supply.GetProperty("status").GetString());
        Assert.Equal(state == "complete" ? JsonValueKind.Number : JsonValueKind.Null, supply.GetProperty("weightedScore").ValueKind);
    }

    private static Mock<ISpecialistAgent> Agent(string key, string reply)
    {
        var agent = new Mock<ISpecialistAgent>();
        agent.SetupGet(a => a.Key).Returns(key);
        agent.Setup(a => a.HandleAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(reply, "offline-test", []));
        return agent;
    }

    private static ScorecardOrchestrator Service(ISpecialistAgent[] agents, Mock<IChatClient>? chat = null) =>
        new(agents, (chat ?? new Mock<IChatClient>(MockBehavior.Strict)).Object,
            new AgentDefinition(), NullLogger<ScorecardOrchestrator>.Instance, Dimensions);

    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "offline-test")], "Test")), "Test")));
    }
}

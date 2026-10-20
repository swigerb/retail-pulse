using System.ClientModel.Primitives;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using RetailPulse.Api.OpenAI.Gateway;
using RetailPulse.Contracts.Gateway;

namespace RetailPulse.Tests.OpenAI;

public sealed class GatewayBudgetEvidenceTests
{
    private static Func<string, string?> Headers(params (string Name, string Value)[] pairs) =>
        name => pairs.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    [Fact]
    public void Parse_ThrottledPreBackend429_KeepsScopeModeRetryRemaining_AndFabricatesNoConsumed()
    {
        GatewayCallEvidence e = GatewayEvidenceHeaders.Parse(Headers(
            ("x-rp-gw-quota", "throttled"),
            ("x-rp-gw-tokens-remaining", "0"),
            ("x-rp-gw-budget-scope", "store"),
            ("x-rp-gw-budget-mode", "demo"),
            ("x-rp-gw-retry-after", "42")), 429);

        e.Quota.Should().Be(GatewayOutcome.Throttled);
        e.TokensConsumed.Should().BeNull();
        e.TokensRemaining.Should().Be(0);
        e.Budget.Should().Be(new GatewayBudgetEvidence(GatewayBudgetScopeKind.Store, GatewayBudgetMode.Demo, 42));
        e.Status.Should().Be(GatewayEvidenceStatus.Observed);
    }

    [Theory]
    [InlineData("store", "normal", true)]
    [InlineData("channel", "demo", true)]
    [InlineData("application", "normal", true)]
    [InlineData("b2b", "demo", true)]
    [InlineData("sub-8f3a", "demo", false)]
    [InlineData("store", "turbo", false)]
    [InlineData("", "demo", false)]
    public void ParseBudget_AcceptsOnlyFixedVocabulary(string scope, string mode, bool accepted)
    {
        GatewayBudgetEvidence? b = GatewayEvidenceHeaders.ParseBudget(Headers(("x-rp-gw-budget-scope", scope), ("x-rp-gw-budget-mode", mode)));
        (b is not null).Should().Be(accepted);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("12.5")]
    [InlineData("abc")]
    [InlineData("86401")]
    [InlineData("")]
    public void ParseBudget_RetryAfterMustBeBoundedNumber_ElseUnknown(string retry)
    {
        GatewayBudgetEvidence? b = GatewayEvidenceHeaders.ParseBudget(
            Headers(("x-rp-gw-budget-scope", "store"), ("x-rp-gw-budget-mode", "normal"), ("x-rp-gw-retry-after", retry)));
        b!.RetryAfterSeconds.Should().BeNull();
    }

    [Fact]
    public void Budget_SerializesWithoutIdentifiers()
    {
        GatewayCallEvidence e = GatewayEvidenceHeaders.Parse(Headers(
            ("x-rp-gw-budget-scope", "b2b"), ("x-rp-gw-budget-mode", "normal")), 200);
        string json = JsonSerializer.Serialize(e, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Should().Contain("\"budget\":{\"scopeKind\":\"b2b\",\"mode\":\"normal\",\"retryAfterSeconds\":null}");
        json.Should().NotContainEquivalentOf("subscription").And.NotContainEquivalentOf("counter");
    }

    [Fact]
    public void ClassifyRateLimit_BudgetThrottle_GetsDistinctCodeRetryAfterAndEvidence()
    {
        GatewayCallEvidence call = GatewayEvidenceHeaders.Parse(Headers(
            ("x-rp-gw-quota", "throttled"), ("x-rp-gw-budget-scope", "channel"),
            ("x-rp-gw-budget-mode", "demo"), ("x-rp-gw-retry-after", "30")), 429);
        var evidence = new GatewayEvidence(GatewayEvidenceStatus.Observed, 1, null, null, [call]);

        GatewayErrorShaper.RateLimited shaped = GatewayErrorShaper.ClassifyRateLimit(evidence);

        shaped.Code.Should().Be(GatewayErrorShaper.TokenBudgetExceeded);
        shaped.RetryAfterSeconds.Should().Be(30);
        shaped.Message.Should().Contain("channel").And.Contain("30 seconds").And.Contain("demo");
        shaped.Gateway.Should().BeSameAs(evidence);
    }

    [Fact]
    public void ClassifyRateLimit_WithoutBudgetEvidence_KeepsGenericRateLimited()
    {
        GatewayErrorShaper.RateLimited shaped = GatewayErrorShaper.ClassifyRateLimit(GatewayEvidence.None);
        shaped.Code.Should().Be("rate_limited");
        shaped.RetryAfterSeconds.Should().BeNull();
    }

    [Fact]
    public async Task BudgetThrottle429_IsNotRetriedByTheSdk_AndEvidenceIsRecordedOnce()
    {
        int calls = 0;
        GatewayEvidenceCollector.Scope scope = GatewayEvidenceCollector.Begin();
        try
        {
            var handler = new StubHandler(_ =>
            {
                calls++;
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                r.Headers.Add("x-rp-gw-quota", "throttled");
                r.Headers.Add("x-rp-gw-budget-scope", "store");
                r.Headers.Add("x-rp-gw-budget-mode", "demo");
                r.Headers.Add("x-rp-gw-retry-after", "42");
                r.Headers.Add("Retry-After", "42");
                return r;
            });
            var pipeline = ClientPipeline.Create(
                new ClientPipelineOptions { Transport = new HttpClientPipelineTransport(new HttpClient(handler)), RetryPolicy = new BudgetAwareRetryPolicy(2) },
                [], [new GatewayEvidencePolicy(GatewayMode.Apim)], []);
            PipelineMessage message = pipeline.CreateMessage();
            message.Request.Uri = new Uri("https://example.test/x");
            message.Request.Method = "POST";
            message.BufferResponse = false;

            await pipeline.SendAsync(message);

            calls.Should().Be(1);
            GatewayEvidence snapshot = scope.Snapshot();
            snapshot.Calls.Should().ContainSingle().Which.Budget!.RetryAfterSeconds.Should().Be(42);
            snapshot.TotalTokensConsumed.Should().BeNull();
        }
        finally
        {
            GatewayEvidenceCollector.End();
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> behavior) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(behavior(request));
    }
}

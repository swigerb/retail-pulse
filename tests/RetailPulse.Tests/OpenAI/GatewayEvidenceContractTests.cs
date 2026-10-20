using System.ClientModel.Primitives;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using RetailPulse.Api.OpenAI.Gateway;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Gateway;

namespace RetailPulse.Tests.OpenAI;

public sealed partial class GatewayEvidenceContractTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> behavior) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => behavior(request);
    }

    private static async Task<GatewayEvidence> RunAsync(GatewayMode mode, Func<HttpRequestMessage, Task<HttpResponseMessage>> behavior, bool expectThrow = false)
    {
        GatewayEvidenceCollector.Scope scope = GatewayEvidenceCollector.Begin();
        try
        {
            var pipeline = ClientPipeline.Create(
                new ClientPipelineOptions { Transport = new HttpClientPipelineTransport(new HttpClient(new StubHandler(behavior))), RetryPolicy = new ClientRetryPolicy(0) },
                [], [new GatewayEvidencePolicy(mode)], []);
            PipelineMessage message = pipeline.CreateMessage();
            message.Request.Uri = new Uri("https://example.test/x");
            message.Request.Method = "POST";
            message.BufferResponse = false;
            Func<Task> send = async () => await pipeline.SendAsync(message);
            if (expectThrow)
            {
                await send.Should().ThrowAsync<Exception>();
            }
            else
            {
                await send();
            }

            return scope.Snapshot();
        }
        finally
        {
            GatewayEvidenceCollector.End();
        }
    }

    private static Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond(int status, Dictionary<string, string>? headers = null) =>
        _ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{}") };
            foreach ((string? k, string? v) in headers ?? [])
            {
                response.Headers.TryAddWithoutValidation(k, v);
            }

            return Task.FromResult(response);
        };

    [Fact]
    public async Task ApimMode_WithHeaders_IsObserved()
    {
        GatewayEvidence e = await RunAsync(GatewayMode.Apim, Respond(200, new()
        {
            [GatewayEvidenceHeaders.RequestId] = "r-1",
            [GatewayEvidenceHeaders.TokensConsumed] = "7",
        }));

        e.Status.Should().Be(GatewayEvidenceStatus.Observed);
        e.Calls.Single().Status.Should().Be(GatewayEvidenceStatus.Observed);
        e.TotalTokensConsumed.Should().Be(7);
    }

    [Fact]
    public async Task ApimMode_MissingHeaders_IsUnavailableNotObserved()
    {
        GatewayEvidence e = await RunAsync(GatewayMode.Apim, Respond(200));

        e.Status.Should().Be(GatewayEvidenceStatus.Unavailable);
        e.Observed.Should().BeFalse();
        e.Calls.Single().Quota.Should().Be(GatewayOutcome.Unknown);
    }

    [Theory]
    [InlineData(GatewayMode.Direct)]
    public async Task NonApimMode_IgnoresInjectedHeaders_AndReportsBypassed(GatewayMode mode)
    {
        GatewayEvidence e = await RunAsync(mode, Respond(200, new()
        {
            [GatewayEvidenceHeaders.RequestId] = "forged",
            [GatewayEvidenceHeaders.Quota] = "allowed",
            [GatewayEvidenceHeaders.Safety] = "passed",
            [GatewayEvidenceHeaders.TokensConsumed] = "999",
        }));

        e.Status.Should().Be(GatewayEvidenceStatus.Bypassed);
        e.Observed.Should().BeFalse();
        GatewayCallEvidence call = e.Calls.Single();
        call.GatewayRequestId.Should().BeNull();
        call.Quota.Should().Be(GatewayOutcome.Unknown);
        call.Safety.Should().Be(GatewayOutcome.Unknown);
        e.TotalTokensConsumed.Should().BeNull();
        call.StatusCode.Should().Be(200);
    }

    [Theory]
    [InlineData(null, GatewayMode.Direct)]
    [InlineData("", GatewayMode.Direct)]
    [InlineData("bogus", GatewayMode.Direct)]
    [InlineData("Direct", GatewayMode.Direct)]
    [InlineData("apim", GatewayMode.Apim)]
    [InlineData("Apim", GatewayMode.Apim)]
    public void GatewayMode_IsTrustedOnlyWhenExplicitlyConfigured(string? value, GatewayMode expected)
    {
        IConfigurationRoot cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(value is null ? [] : new Dictionary<string, string?> { [GatewayModeSettings.ConfigKey] = value })
            .Build();

        GatewayModeSettings.Resolve(cfg).Should().Be(expected);
    }

    [Fact]
    public async Task ApimMode_SafetyBlockedHeader_IsBlocked()
    {
        GatewayEvidence e = await RunAsync(GatewayMode.Apim, Respond(400, new()
        {
            [GatewayEvidenceHeaders.RequestId] = "r-2",
            [GatewayEvidenceHeaders.Safety] = "blocked",
        }));

        e.Status.Should().Be(GatewayEvidenceStatus.Blocked);
        e.Calls.Single().Status.Should().Be(GatewayEvidenceStatus.Blocked);
        e.Calls.Single().StatusCode.Should().Be(400);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task NonOkStatuses_KeepEvidenceAndStatusCode(int status)
    {
        GatewayEvidence e = await RunAsync(GatewayMode.Apim, Respond(status, new()
        {
            [GatewayEvidenceHeaders.RequestId] = "r-3",
            [GatewayEvidenceHeaders.Quota] = "throttled",
        }));

        GatewayCallEvidence call = e.Calls.Single();
        call.StatusCode.Should().Be(status);
        call.Quota.Should().Be(GatewayOutcome.Throttled);
        e.Status.Should().Be(GatewayEvidenceStatus.Observed);
    }

    [Fact]
    public async Task MalformedAndRedactedHeaders_BecomeUnknown()
    {
        GatewayEvidence e = await RunAsync(GatewayMode.Apim, Respond(200, new()
        {
            [GatewayEvidenceHeaders.RequestId] = "sk secret value!",
            [GatewayEvidenceHeaders.TokensConsumed] = "-1",
            [GatewayEvidenceHeaders.Quota] = "bogus",
        }));

        e.Status.Should().Be(GatewayEvidenceStatus.Unavailable);
        e.Calls.Single().GatewayRequestId.Should().BeNull();
    }

    [Theory]
    [InlineData(typeof(HttpRequestException), "network")]
    [InlineData(typeof(TimeoutException), "timeout")]
    [InlineData(typeof(TaskCanceledException), "canceled")]
    [InlineData(typeof(InvalidOperationException), "other")]
    public async Task TransportFailure_IsRecordedWithClassOnly_AndRethrown(Type exceptionType, string expectedClass)
    {
        const string secret = "Bearer sk-SECRET host=10.1.2.3 prompt=hello";
        GatewayEvidence e = await RunAsync(GatewayMode.Apim,
            _ => throw (Exception)Activator.CreateInstance(exceptionType, secret)!,
            expectThrow: true);

        e.Status.Should().Be(GatewayEvidenceStatus.Failed);
        GatewayCallEvidence call = e.Calls.Single();
        call.Status.Should().Be(GatewayEvidenceStatus.Failed);
        call.TransportError.Should().Be(expectedClass);
        JsonSerializer.Serialize(e, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Should().NotContain("SECRET").And.NotContain("10.1.2.3").And.NotContain("prompt");
    }

    [Fact]
    public void Aggregate_FailureAndBlockedOutrankObserved_AndMixedIsNotObserved()
    {
        GatewayEvidenceCollector.Scope scope = GatewayEvidenceCollector.Begin();
        GatewayEvidenceCollector.Record(GatewayEvidenceHeaders.Parse(n => n == GatewayEvidenceHeaders.RequestId ? "a" : null, 200));
        scope.Snapshot().Status.Should().Be(GatewayEvidenceStatus.Observed);

        GatewayEvidenceCollector.Record(GatewayCallEvidence.Empty(GatewayEvidenceStatus.Unavailable, 200));
        scope.Snapshot().Status.Should().Be(GatewayEvidenceStatus.Unavailable);

        GatewayEvidenceCollector.Record(GatewayCallEvidence.Empty(GatewayEvidenceStatus.Blocked, 400));
        scope.Snapshot().Status.Should().Be(GatewayEvidenceStatus.Blocked);

        GatewayEvidenceCollector.Record(GatewayCallEvidence.Empty(GatewayEvidenceStatus.Failed, null, "network"));
        scope.Snapshot().Status.Should().Be(GatewayEvidenceStatus.Failed);
        GatewayEvidenceCollector.End();
    }

    [Fact]
    public void Aggregate_AllBypassed_IsBypassed()
    {
        GatewayEvidenceCollector.Scope scope = GatewayEvidenceCollector.Begin();
        GatewayEvidenceCollector.Record(GatewayCallEvidence.Empty(GatewayEvidenceStatus.Bypassed, 200));
        scope.Snapshot().Status.Should().Be(GatewayEvidenceStatus.Bypassed);
        GatewayEvidenceCollector.End();
    }

    [Fact]
    public void Truncation_IsSurfaced_AndDroppedFailuresStillCount()
    {
        GatewayEvidenceCollector.Scope scope = GatewayEvidenceCollector.Begin();
        int cap = GatewayEvidenceCollector.MaxCallsPerScope;
        for (int i = 0; i < cap; i++)
        {
            GatewayEvidenceCollector.Record(GatewayEvidenceHeaders.Parse(n => n == GatewayEvidenceHeaders.RequestId ? "a" : null, 200));
        }

        GatewayEvidenceCollector.Record(GatewayCallEvidence.Empty(GatewayEvidenceStatus.Failed, null, "timeout"));
        GatewayEvidenceCollector.Record(GatewayEvidenceHeaders.Parse(n => n == GatewayEvidenceHeaders.RequestId ? "a" : null, 200));

        GatewayEvidence snap = scope.Snapshot();

        snap.Truncated.Should().BeTrue();
        snap.DroppedCalls.Should().Be(2);
        snap.CallCount.Should().Be(cap + 2);
        snap.Calls.Should().HaveCount(cap);
        snap.Status.Should().Be(GatewayEvidenceStatus.Failed);
        GatewayEvidenceCollector.End();
    }

    [Fact]
    public void NoTruncation_ReportsFalse()
    {
        GatewayEvidenceCollector.Scope scope = GatewayEvidenceCollector.Begin();
        GatewayEvidenceCollector.Record(GatewayCallEvidence.Empty(GatewayEvidenceStatus.Bypassed));
        scope.Snapshot().Truncated.Should().BeFalse();
        GatewayEvidenceCollector.End();
    }

    [Fact]
    public async Task ConcurrentScopes_DoNotCrossContaminate()
    {
        async Task<GatewayEvidence> Request(string id, long tokens, int calls)
        {
            GatewayEvidenceCollector.Scope scope = GatewayEvidenceCollector.Begin();
            await Task.WhenAll(Enumerable.Range(0, calls).Select(async i =>
            {
                await Task.Delay(Random.Shared.Next(1, 10));
                await Task.Yield();
                GatewayEvidenceCollector.Record(GatewayEvidenceHeaders.Parse(n => n switch
                {
                    GatewayEvidenceHeaders.RequestId => id,
                    GatewayEvidenceHeaders.TokensConsumed => tokens.ToString(),
                    _ => null,
                }, 200));
            }));
            return scope.Snapshot();
        }

        GatewayEvidence[] results = await Task.WhenAll(
            Task.Run(() => Request("req-a", 10, 5)),
            Task.Run(() => Request("req-b", 100, 8)));

        results[0].CallCount.Should().Be(5);
        results[0].Calls.Should().OnlyContain(c => c.GatewayRequestId == "req-a");
        results[0].TotalTokensConsumed.Should().Be(50);
        results[1].CallCount.Should().Be(8);
        results[1].Calls.Should().OnlyContain(c => c.GatewayRequestId == "req-b");
        results[1].TotalTokensConsumed.Should().Be(800);
    }

    private static async Task<object?> RunFilter(object? handlerResult, Action? inHandler = null)
    {
        var ctx = new DefaultEndpointFilterInvocationContext(new DefaultHttpContext());
        return await GatewayEvidenceExtensions.ApplyAsync(ctx, _ =>
        {
            inHandler?.Invoke();
            return ValueTask.FromResult(handlerResult);
        });
    }

    [Fact]
    public async Task EndpointFilter_AttachesSnapshot_ToOkChatResponse()
    {
        object? result = await RunFilter(
            TypedResults.Ok(new ChatResponse("reply", "s", [])),
            () => GatewayEvidenceCollector.Record(GatewayCallEvidence.Empty(GatewayEvidenceStatus.Bypassed, 200)));

        Ok<ChatResponse> ok = result.Should().BeOfType<Ok<ChatResponse>>().Subject;
        ok.Value!.Reply.Should().Be("reply");
        ok.Value.Gateway.Should().NotBeNull();
        ok.Value.Gateway!.Status.Should().Be(GatewayEvidenceStatus.Bypassed);
    }

    [Fact]
    public async Task EndpointFilter_NoCalls_AttachesNone_AndNonOkPassThrough()
    {
        Ok<ChatResponse> ok = (await RunFilter(TypedResults.Ok(new ChatResponse("r", "s", [])))).Should().BeOfType<Ok<ChatResponse>>().Subject;
        ok.Value!.Gateway.Should().BeSameAs(GatewayEvidence.None);

        Accepted accepted = TypedResults.Accepted("/x");
        (await RunFilter(accepted)).Should().BeSameAs(accepted);
        StatusCodeHttpResult status = TypedResults.StatusCode(429);
        (await RunFilter(status)).Should().BeSameAs(status);
        ProblemHttpResult problem = TypedResults.Problem(statusCode: 503);
        (await RunFilter(problem)).Should().BeSameAs(problem);
    }

    [Fact]
    public async Task EndpointFilter_ClearsScope_AfterRequest()
    {
        await RunFilter(TypedResults.Ok(new ChatResponse("r", "s", [])));

        GatewayEvidenceCollector.Record(GatewayCallEvidence.Empty(GatewayEvidenceStatus.Failed, null, "other"));
        GatewayEvidenceCollector.Scope scope = GatewayEvidenceCollector.Begin();
        scope.Snapshot().CallCount.Should().Be(0);
        GatewayEvidenceCollector.End();
    }

    [Fact]
    public void ChatEndpoints_ApplyGatewayEvidence_ToChatAndStream()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "src", "RetailPulse.Api", "Endpoints", "ChatEndpoints.cs")))
            dir = Path.GetDirectoryName(dir)!;
        string src = File.ReadAllText(Path.Combine(dir, "src", "RetailPulse.Api", "Endpoints", "ChatEndpoints.cs"));

        MyRegex().Count(src).Should().Be(2);
        int stream = src.IndexOf("\"/api/chat/stream\"", StringComparison.Ordinal);
        src.IndexOf(".WithGatewayEvidence()", stream, StringComparison.Ordinal).Should().BePositive();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\.WithGatewayEvidence\(\)")]
    private static partial System.Text.RegularExpressions.Regex MyRegex();
}


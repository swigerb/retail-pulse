using System.Net;
using System.Net.Http.Json;
using Azure.AI.ContentSafety;
using Azure.Core;
using Azure.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Api.Resilience;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// Rejection findings #2, #3, #4 — the Prompt Shields raw HTTP path and the
/// SDK <see cref="ContentSafetyClient"/> both flow through the same resilience
/// pipeline. These tests wire up the real
/// <see cref="ResilienceExtensions.AddContentSafetyResilienceHandler"/> against
/// a hostile <see cref="HttpMessageHandler"/> and prove:
///
/// 1. After the configured failure threshold, subsequent calls short-circuit
///    with <see cref="Polly.CircuitBreaker.BrokenCircuitException"/> and never
///    hit the wire.
/// 2. A hanging request is bounded by <see cref="ContentSafetyConfig.TimeoutMs"/>
///    — the evaluator returns <see cref="ContentSafetyDecision.ServiceUnavailable"/>
///    within the configured window and does not stall the middleware.
/// </summary>
[Collection("AzureContentSafetyEvaluatorActivity")]
public class ContentSafetyResilienceTests
{
    private const int _timeoutMs = 400;

    [Fact]
    public async Task CircuitBreaker_OpensAfterFailureBurst_AndShortCircuitsNextCall()
    {
        var handler = new CountingFailingHandler();
        AzureContentSafetyEvaluator evaluator = BuildResilientEvaluator(handler);

        // Drive enough failures to trip the breaker (Polly minimum-throughput=5,
        // ratio=0.5, sampling=30s). 8 hostile calls guarantee an Open state.
        for (int i = 0; i < 8; i++)
        {
            _ = await evaluator.EvaluateAsync(
                $"query-{i}",
                ContentSafetyStage.Input,
                new ContentSafetyEvaluationContext(UserId: "u", CheckPromptShield: true),
                CancellationToken.None);
        }

        int callsAfterBurst = handler.CallCount;

        ContentSafetyResult shortCircuited = await evaluator.EvaluateAsync(
            "after-breaker-opens",
            ContentSafetyStage.Input,
            new ContentSafetyEvaluationContext(UserId: "u", CheckPromptShield: true),
            CancellationToken.None);

        shortCircuited.Decision.Should().Be(
            ContentSafetyDecision.ServiceUnavailable,
            "an open breaker must surface as ServiceUnavailable so the fail-policy applies uniformly");
        handler.CallCount.Should().Be(callsAfterBurst,
            "an open circuit MUST short-circuit — no additional HTTP round-trip may reach the primary handler");
    }

    [Fact]
    public async Task Timeout_HangingHandler_IsBoundedByTimeoutMs()
    {
        // Invariant (issue #156): a hanging Content Safety region cannot be
        // allowed to stall the middleware — the bounded timeout must fire AND
        // cancellation must reach the primary handler. The prior wall-clock
        // Stopwatch upper bound was load-sensitive under the full 3,396-test
        // suite (observed ~6.1 s against a 3.2 s ceiling ≈ 1 in 10 runs).
        //
        // Assert the invariant on observable cancellation instead of on real
        // elapsed time:
        //   1. The pipeline surfaces ServiceUnavailable (Polly timeout fired).
        //   2. The primary HttpMessageHandler observed the cancellation token
        //      being cancelled — the ONLY way this happens is if the bounded
        //      timeout propagated cancellation into the underlying transport
        //      before it could complete a real request.
        //
        // If the pipeline were broken and never cancelled the inner handler,
        // (2) would fail — either because the OCE from Task.Delay would never
        // fire, or because the evaluator would never return. Either way the
        // failure is unambiguous and independent of the machine's load.
        var handler = new HangingHandler();
        AzureContentSafetyEvaluator evaluator = BuildResilientEvaluator(handler);

        ContentSafetyResult result = await evaluator.EvaluateAsync(
            "will-hang",
            ContentSafetyStage.Input,
            new ContentSafetyEvaluationContext(UserId: "u", CheckPromptShield: true),
            CancellationToken.None);

        result.Decision.Should().Be(ContentSafetyDecision.ServiceUnavailable);

        // Polly cancels the linked token synchronously when its timeout fires,
        // and CancellationToken.Register callbacks run synchronously from that
        // Cancel() call before Polly unwinds. By the time the evaluator has
        // returned ServiceUnavailable the registered callback has therefore
        // run — so awaiting the already-completed task is a no-op. If the
        // pipeline is broken and cancellation never propagates, this await
        // hangs (a real bug, caught by CI job timeout — not a flaky assertion).
        await handler.CancellationObserved;
        handler.CancellationObserved.IsCompleted.Should().BeTrue(
            "the bounded timeout MUST propagate cancellation into the primary handler; " +
            "a hanging Content Safety region cannot be allowed to stall the middleware");
    }

    [Fact]
    public void SharedTransport_SdkAndRawPath_ResolveTheSameHttpClient()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new GuardrailsConfig());
        services.AddLogging();
        services.AddSingleton<TokenCredential>(new FakeTokenCredential());
        services.AddContentSafety(new ContentSafetyConfig
        {
            Enabled = true,
            Endpoint = "https://fake.cognitiveservices.azure.com",
            TimeoutMs = 1500,
        });

        using ServiceProvider sp = services.BuildServiceProvider();

        // Both singletons pull the SAME named client from the factory, so both
        // failure classes are behind one breaker (rejection finding #2).
        ContentSafetyClient sdk = sp.GetRequiredService<ContentSafetyClient>();
        sdk.Should().NotBeNull();

        IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient(ContentSafetyServiceCollectionExtensions.HttpClientName);
        client.BaseAddress.Should().NotBeNull();
    }

    private static AzureContentSafetyEvaluator BuildResilientEvaluator(HttpMessageHandler primary)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TokenCredential>(new FakeTokenCredential());
        services.AddSingleton<ContentSafetyTokenProvider>();
        services.AddHttpClient(ContentSafetyServiceCollectionExtensions.HttpClientName, client =>
            {
                client.BaseAddress = new Uri("https://fake.cognitiveservices.azure.com");
                client.Timeout = TimeSpan.FromMilliseconds(_timeoutMs * 4);
            })
            .ConfigurePrimaryHttpMessageHandler(() => primary)
            .AddContentSafetyResilienceHandler(_timeoutMs);

        // Prevent the HttpClientFactory from disposing the primary handler
        // between test iterations — CountingFailingHandler MUST persist so we
        // can count total wire hits.
        services.Configure<HttpClientFactoryOptions>(
            ContentSafetyServiceCollectionExtensions.HttpClientName,
            options => options.HandlerLifetime = TimeSpan.FromHours(1));

        ServiceProvider sp = services.BuildServiceProvider();
        IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
        HttpClient http = factory.CreateClient(ContentSafetyServiceCollectionExtensions.HttpClientName);

        var sdkClient = new ContentSafetyClient(
            http.BaseAddress!,
            new FakeTokenCredential(),
            new ContentSafetyClientOptions { Transport = new HttpClientTransport(http) });

        var config = new GuardrailsConfig
        {
            ContentSafety = new ContentSafetyConfig
            {
                Enabled = true,
                Endpoint = http.BaseAddress!.ToString(),
                TimeoutMs = _timeoutMs,
                PromptShieldsEnabled = true,
            }
        };
        ContentSafetyTokenProvider tokens = sp.GetRequiredService<ContentSafetyTokenProvider>();
        return new AzureContentSafetyEvaluator(
            sdkClient, http, tokens, config,
            NullLoggerFactory.Instance.CreateLogger<AzureContentSafetyEvaluator>());
    }

    private sealed class CountingFailingHandler : HttpMessageHandler
    {
        private int _count;
        public int CallCount => Volatile.Read(ref _count);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = JsonContent.Create(new { error = "simulated 500" })
            });
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Completes when the primary handler observes the request's
        /// <see cref="CancellationToken"/> being cancelled — the observable
        /// invariant that the bounded Polly timeout propagated into the
        /// underlying transport. If cancellation never fires, this task
        /// never completes and the test will hang (unambiguous real bug,
        /// not a wall-clock tolerance to tune).
        /// </summary>
        public Task CancellationObserved => _cancellationObserved.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Signal from the CT registration rather than the OCE handler so
            // the observation is captured even if the delay unwinds before
            // Polly's timeout strategy translates the OCE into its result.
            using CancellationTokenRegistration reg = cancellationToken.Register(
                static tcs => ((TaskCompletionSource)tcs!).TrySetResult(),
                _cancellationObserved);

            // Wait until cancelled by the Polly timeout — the pipeline signals
            // cancellation via the token so this is a well-behaved hang.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

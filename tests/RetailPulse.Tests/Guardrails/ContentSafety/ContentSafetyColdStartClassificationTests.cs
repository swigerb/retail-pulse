using System.Net;
using System.Net.Http.Json;
using Azure.AI.ContentSafety;
using Azure.Core;
using Azure.Identity;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// Cold-start classification behaviour of <see cref="AzureContentSafetyEvaluator"/>.
///
/// The original defect had two halves. First, the managed-identity token fetch
/// ran inside the per-scan timeout, so the unprimed first fetch blew the scan
/// budget and every cold-start scan failed open. Second, every failure collapsed
/// into one generic "unreachable" outcome, so an operator could not tell a
/// timeout from a 401. These tests pin the fix for both:
///
/// * A slow token fetch that stays within its own (separate) budget no longer
///   consumes the scan budget, so the scan still passes.
/// * A token timeout, an authentication rejection, and a transport failure each
///   produce a distinct <see cref="ContentSafetyFailureReason"/>.
/// * An authentication rejection is not retried.
/// </summary>
[Collection("AzureContentSafetyEvaluatorActivity")]
public class ContentSafetyColdStartClassificationTests
{
    [Fact]
    public async Task SlowTokenWithinItsOwnBudget_DoesNotConsumeScanBudget()
    {
        // Token fetch is deliberately slower than the scan floor (200 ms) but well
        // inside the token budget. If the two budgets were still shared this scan
        // would time out and fail open; with them separated it must pass.
        var slowProviderCredential = new ProgrammableTokenCredential(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(350), ct);
            return ProgrammableTokenCredential.FreshToken("primed-after-delay");
        });

        AzureContentSafetyEvaluator evaluator = BuildEvaluator(
            new CapturingHttpMessageHandler(),
            providerCredential: slowProviderCredential,
            sdkCredential: new FakeTokenCredential(token: "sdk-instant"),
            configure: cfg =>
            {
                cfg.TimeoutMs = 1;          // scan floor clamps to 200 ms
                cfg.TokenTimeoutMs = 5000;  // token fetch has its own generous budget
            });

        ContentSafetyResult result = await evaluator.EvaluateAsync(
            "benign text",
            ContentSafetyStage.Input,
            new ContentSafetyEvaluationContext(UserId: "u", CheckPromptShield: true),
            CancellationToken.None);

        result.Decision.Should().Be(ContentSafetyDecision.Passed,
            "a slow first-token fetch inside its own budget must not steal the scan timeout");
    }

    [Fact]
    public async Task AuthenticationRejection_IsClassifiedAsAuthentication_AndNotRetried()
    {
        var rejectingCredential = new ProgrammableTokenCredential((_, _) =>
            throw new AuthenticationFailedException("managed identity is not authorised"));

        AzureContentSafetyEvaluator evaluator = BuildEvaluator(
            new CapturingHttpMessageHandler(),
            providerCredential: rejectingCredential,
            sdkCredential: rejectingCredential,
            configure: cfg => cfg.TokenTimeoutMs = 5000);

        ContentSafetyResult result = await evaluator.EvaluateAsync(
            "benign text",
            ContentSafetyStage.Input,
            new ContentSafetyEvaluationContext(UserId: "u", CheckPromptShield: true),
            CancellationToken.None);

        result.Decision.Should().Be(ContentSafetyDecision.ServiceUnavailable);
        result.FailureReason.Should().Be(ContentSafetyFailureReason.Authentication,
            "a rejected identity must be distinguishable from a timeout in the audit reason");
        rejectingCredential.Calls.Should().Be(1,
            "an authentication rejection is terminal and must not be retried inside the evaluator");
    }

    [Fact]
    public async Task TokenFetchExceedingTokenBudget_IsClassifiedAsTimeout()
    {
        var hangingCredential = new ProgrammableTokenCredential(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return default;
        });

        AzureContentSafetyEvaluator evaluator = BuildEvaluator(
            new CapturingHttpMessageHandler(),
            providerCredential: hangingCredential,
            sdkCredential: new FakeTokenCredential(),
            configure: cfg =>
            {
                cfg.TimeoutMs = 1500;
                cfg.TokenTimeoutMs = 250; // token budget is what expires here
            });

        ContentSafetyResult result = await evaluator.EvaluateAsync(
            "benign text",
            ContentSafetyStage.Input,
            new ContentSafetyEvaluationContext(UserId: "u", CheckPromptShield: true),
            CancellationToken.None);

        result.Decision.Should().Be(ContentSafetyDecision.ServiceUnavailable);
        result.FailureReason.Should().Be(ContentSafetyFailureReason.Timeout,
            "a token fetch that blows its own budget is a timeout, not an auth failure");
    }

    [Fact]
    public async Task TransportFailure_IsClassifiedAsTransport()
    {
        var handler = new CapturingHttpMessageHandler
        {
            Responder = (req, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
        };

        AzureContentSafetyEvaluator evaluator = BuildEvaluator(
            handler,
            providerCredential: new FakeTokenCredential(token: "ok"),
            sdkCredential: new FakeTokenCredential(token: "ok"),
            configure: cfg =>
            {
                cfg.TimeoutMs = 1500;
                cfg.TokenTimeoutMs = 5000;
            });

        ContentSafetyResult result = await evaluator.EvaluateAsync(
            "benign text",
            ContentSafetyStage.Input,
            new ContentSafetyEvaluationContext(UserId: "u", CheckPromptShield: true),
            CancellationToken.None);

        result.Decision.Should().Be(ContentSafetyDecision.ServiceUnavailable);
        result.FailureReason.Should().Be(ContentSafetyFailureReason.Transport,
            "an HTTP failure with a token in hand is a transport problem, not an auth or timeout one");
    }

    private static AzureContentSafetyEvaluator BuildEvaluator(
        CapturingHttpMessageHandler handler,
        TokenCredential providerCredential,
        TokenCredential sdkCredential,
        Action<ContentSafetyConfig> configure)
    {
        var http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://fake.cognitiveservices.azure.com"),
        };

        // Default the Prompt Shields + AnalyzeText responses to benign shapes so
        // the only variable under test is the failure classification path.
        handler.Responder ??= (req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("text:analyze", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { categoriesAnalysis = Array.Empty<object>() }),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    userPromptAnalysis = new { attackDetected = false },
                    documentsAnalysis = Array.Empty<object>(),
                }),
            });
        };

        var sdkOptions = new ContentSafetyClientOptions
        {
            Transport = new Azure.Core.Pipeline.HttpClientTransport(http),
        };
        var sdkClient = new ContentSafetyClient(http.BaseAddress!, sdkCredential, sdkOptions);

        var config = new GuardrailsConfig
        {
            ContentSafety = new ContentSafetyConfig
            {
                Enabled = true,
                Endpoint = http.BaseAddress!.ToString(),
                PromptShieldsEnabled = true,
            },
        };
        configure(config.ContentSafety);

        var tokens = new ContentSafetyTokenProvider(providerCredential);
        return new AzureContentSafetyEvaluator(
            sdkClient, http, tokens, config,
            NullLoggerFactory.Instance.CreateLogger<AzureContentSafetyEvaluator>());
    }
}

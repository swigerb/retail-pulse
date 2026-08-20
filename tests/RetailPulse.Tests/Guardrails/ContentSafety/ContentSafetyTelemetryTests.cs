using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Azure.AI.ContentSafety;
using Azure.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// Rejection finding #5 — every Content Safety stage MUST emit its own
/// span with a stable name and tag schema. These tests attach an
/// <see cref="ActivityListener"/> to the shared <c>RetailPulse.Agent</c>
/// source and verify that the four expected span names and their tag set
/// arrive on the wire.
/// </summary>
[Collection("AzureContentSafetyEvaluatorActivity")]
public class ContentSafetyTelemetryTests
{
    [Theory]
    [InlineData(ContentSafetyStage.Input, "guardrails.contentsafety.input")]
    [InlineData(ContentSafetyStage.Output, "guardrails.contentsafety.output")]
    [InlineData(ContentSafetyStage.RetrievedKnowledge, "guardrails.contentsafety.retrieved_knowledge")]
    [InlineData(ContentSafetyStage.ToolResult, "guardrails.contentsafety.tool_result")]
    public async Task Evaluator_EmitsExpectedSpanNameAndCoreTags_ForEachStage(
        ContentSafetyStage stage, string expectedSpanName)
    {
        var handler = new StubOkHandler();
        AzureContentSafetyEvaluator evaluator = BuildEvaluator(handler);

        using var recorded = new SpanRecorder();

        _ = await evaluator.EvaluateAsync(
            "arbitrary payload",
            stage,
            new ContentSafetyEvaluationContext(
                UserId: "u",
                CheckPromptShield: stage is ContentSafetyStage.Input or ContentSafetyStage.RetrievedKnowledge),
            CancellationToken.None);

        Activity? span = recorded.Activities.LastOrDefault(a => a.OperationName == expectedSpanName);
        span.Should().NotBeNull($"the {stage} stage must emit '{expectedSpanName}'");

        IReadOnlyDictionary<string, object?> tags = span.TagObjects
            .ToDictionary(k => k.Key, v => v.Value);

        tags.Should().ContainKey("guardrails.contentsafety.stage");
        tags.Should().ContainKey("guardrails.contentsafety.decision");
        tags.Should().ContainKey("guardrails.contentsafety.latency_ms");
        tags.Should().ContainKey("guardrails.contentsafety.prompt_shield.jailbreak");
        tags.Should().ContainKey("guardrails.contentsafety.prompt_shield.indirect");
        tags["guardrails.contentsafety.stage"].Should().Be(stage.ToString());
    }

    [Fact]
    public async Task Evaluator_EmitsPerCategorySeverityTag_WhenCategoryHit()
    {
        var handler = new StubOkHandler
        {
            AnalyzeTextCategories =
            [
                new { category = "Hate", severity = 6 }
            ]
        };
        AzureContentSafetyEvaluator evaluator = BuildEvaluator(handler);

        using var recorded = new SpanRecorder();

        _ = await evaluator.EvaluateAsync(
            "hateful content",
            ContentSafetyStage.Input,
            new ContentSafetyEvaluationContext(UserId: "u", CheckPromptShield: true),
            CancellationToken.None);

        Activity span = recorded.Activities.Last(a => a.OperationName == "guardrails.contentsafety.input");
        IReadOnlyDictionary<string, object?> tags = span.TagObjects
            .ToDictionary(k => k.Key, v => v.Value);

        tags.Should().ContainKey("guardrails.contentsafety.category.hate");
        tags["guardrails.contentsafety.category.hate"].Should().Be(6);
    }

    private static AzureContentSafetyEvaluator BuildEvaluator(StubOkHandler handler)
    {
        var http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://fake.cognitiveservices.azure.com")
        };
        var credential = new FakeTokenCredential();
        var tokens = new ContentSafetyTokenProvider(credential);
        var sdk = new ContentSafetyClient(
            http.BaseAddress,
            credential,
            new ContentSafetyClientOptions { Transport = new HttpClientTransport(http) });
        var config = new GuardrailsConfig
        {
            ContentSafety = new ContentSafetyConfig
            {
                Enabled = true,
                Endpoint = http.BaseAddress.ToString(),
                PromptShieldsEnabled = true
            }
        };
        return new AzureContentSafetyEvaluator(
            sdk, http, tokens, config,
            NullLoggerFactory.Instance.CreateLogger<AzureContentSafetyEvaluator>());
    }

    /// <summary>Serves both PromptShield and AnalyzeText requests with configurable category output.</summary>
    private sealed class StubOkHandler : HttpMessageHandler
    {
        public IReadOnlyList<object> AnalyzeTextCategories { get; set; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("text:analyze", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        categoriesAnalysis = AnalyzeTextCategories
                    })
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    userPromptAnalysis = new { attackDetected = false },
                    documentsAnalysis = Array.Empty<object>()
                })
            });
        }
    }

    private sealed class SpanRecorder : IDisposable
    {
        private readonly ActivityListener _listener;
        public List<Activity> Activities { get; } = [];

        public SpanRecorder()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = src => src.Name == "RetailPulse.Agent",
                Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = Activities.Add,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();
    }
}

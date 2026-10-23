using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Azure.AI.ContentSafety;
using Azure.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

[Collection("AzureContentSafetyEvaluatorActivity")]
public sealed class ContentSafetyFailureDiagnosticsTests
{
    private const string _body = "SYNTHETIC-BODY-MUST-NOT-APPEAR-IN-LOGS";
    private const string _bearer = "synthetic-bearer-must-not-appear-in-logs";

    [Theory]
    [InlineData("PromptShield", 403, ContentSafetyFailureReason.Authentication)]
    [InlineData("PromptShield", 503, ContentSafetyFailureReason.Transport)]
    [InlineData("AnalyzeText", 403, ContentSafetyFailureReason.Authentication)]
    [InlineData("AnalyzeText", 503, ContentSafetyFailureReason.Transport)]
    public async Task HttpFailure_ReportsPhaseStatusAndBudgets_WithoutBodiesOrException(
        string operation, int status, ContentSafetyFailureReason reason)
    {
        var handler = new CapturingHttpMessageHandler
        {
            Responder = (request, _) =>
            {
                bool fail = operation == "AnalyzeText"
                    ? request.RequestUri!.AbsolutePath.EndsWith("text:analyze", StringComparison.Ordinal)
                    : request.RequestUri!.AbsolutePath.EndsWith("text:shieldPrompt", StringComparison.Ordinal);
                return Task.FromResult(fail
                    ? new HttpResponseMessage((HttpStatusCode)status)
                    {
                        Content = JsonContent.Create(new { error = new { code = "SyntheticFailure", message = _body } }),
                    }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new { userPromptAnalysis = new { attackDetected = false } }),
                    });
            },
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://diagnostics.example.test") };
        var credential = new FakeTokenCredential(_bearer);
        var options = new ContentSafetyClientOptions { Transport = new HttpClientTransport(http) };
        // Isolate the terminal SDK response, not the SDK's own retry schedule.
        options.Retry.MaxRetries = 0;
        var sdk = new ContentSafetyClient(http.BaseAddress, credential, options);
        var logger = new DiagnosticLogger<AzureContentSafetyEvaluator>();
        var config = new GuardrailsConfig
        {
            ContentSafety = new ContentSafetyConfig
            {
                Enabled = true,
                TimeoutMs = 1500,
                TokenTimeoutMs = 5000,
            },
        };
        var evaluator = new AzureContentSafetyEvaluator(
            sdk, http, new ContentSafetyTokenProvider(credential), config, logger);

        ContentSafetyResult result = await evaluator.EvaluateAsync(
            _body, ContentSafetyStage.AgentDefinition,
            new ContentSafetyEvaluationContext(UserId: "startup-validator", CheckPromptShield: true),
            CancellationToken.None);

        result.Decision.Should().Be(ContentSafetyDecision.ServiceUnavailable);
        result.FailureReason.Should().Be(reason);
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Exception.Should().BeNull("an SDK exception can embed the entire response body");
        entry.Fields["Stage"].Should().Be(ContentSafetyStage.AgentDefinition);
        entry.Fields["Operation"].Should().Be(operation);
        entry.Fields["FailureReason"].Should().Be(reason);
        entry.Fields["Status"].Should().Be(status);
        entry.Fields["ScanTimeoutMs"].Should().Be(1500);
        entry.Fields["TokenTimeoutMs"].Should().Be(5000);
        entry.Fields["ExceptionType"].Should().Be(operation == "AnalyzeText" ? "RequestFailedException" : "HttpRequestException");
        entry.Fields["ElapsedMs"].Should().BeOfType<int>().Which.Should().BeGreaterThanOrEqualTo(0);
        string rendered = entry.Message + string.Join(" ", entry.Fields.Values);
        rendered.Should().NotContain(_body).And.NotContain(_bearer).And.NotContain("diagnostics.example.test");
    }

    [Fact]
    public async Task ConnectionFailure_ReportsSafeTransportCodes_WithoutMessages()
    {
        var handler = new CapturingHttpMessageHandler
        {
            Responder = (_, _) => throw new HttpRequestException(
                HttpRequestError.ConnectionError, _body, new SocketException((int)SocketError.ConnectionRefused)),
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://diagnostics.example.test") };
        var credential = new FakeTokenCredential(_bearer);
        var sdk = new ContentSafetyClient(http.BaseAddress, credential,
            new ContentSafetyClientOptions { Transport = new HttpClientTransport(http) });
        var logger = new DiagnosticLogger<AzureContentSafetyEvaluator>();
        var evaluator = new AzureContentSafetyEvaluator(sdk, http, new ContentSafetyTokenProvider(credential),
            new GuardrailsConfig(), logger);

        ContentSafetyResult result = await evaluator.EvaluateAsync(
            _body, ContentSafetyStage.AgentDefinition,
            new ContentSafetyEvaluationContext(UserId: "startup-validator", CheckPromptShield: true), CancellationToken.None);

        result.FailureReason.Should().Be(ContentSafetyFailureReason.Transport);
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Fields["Operation"].Should().Be("PromptShield");
        entry.Fields["Status"].Should().BeNull();
        entry.Fields["HttpRequestError"].Should().Be(HttpRequestError.ConnectionError);
        entry.Fields["CauseType"].Should().Be("SocketException");
        entry.Fields["SocketError"].Should().Be(SocketError.ConnectionRefused);
        entry.Exception.Should().BeNull();
        entry.Message.Should().NotContain(_body).And.NotContain(_bearer);
    }

    [Fact]
    public async Task WarmUpFailure_ReportsBoundedAttempts_WithoutExceptionMessage()
    {
        var credential = new FakeTokenCredential(_bearer);
        var handler = new CapturingHttpMessageHandler
        {
            Responder = (_, _) => throw new HttpRequestException(_body),
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://diagnostics.example.test") };
        var logger = new DiagnosticLogger<ContentSafetyWarmUpService>();
        var service = new ContentSafetyWarmUpService(
            new ContentSafetyTokenProvider(credential), new SingleClientFactory(http),
            new GuardrailsConfig(), logger);

        await service.WarmAsync(CancellationToken.None);

        handler.CallCount.Should().Be(2);
        var entry = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Subject;
        entry.Fields["Attempts"].Should().Be(2);
        entry.Fields["ExceptionType"].Should().Be("HttpRequestException");
        entry.Exception.Should().BeNull();
        entry.Message.Should().NotContain(_body).And.NotContain(_bearer);
    }

    private sealed class SingleClientFactory(HttpClient http) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => http;
    }

    private sealed class DiagnosticLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception, Dictionary<string, object?> Fields)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception,
                ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value)));
    }
}

using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using RetailPulse.Api.Health;

namespace RetailPulse.Tests.Observability;

public class HealthCheckTests
{
    #region McpServerHealthCheck

    [Fact]
    public async Task McpServerHealthCheck_ReturnsHealthy_WhenServerResponds200()
    {
        DelegatingHandler handler = CreateMockHandler(HttpStatusCode.OK);
        Mock<IHttpClientFactory> factory = CreateFactory(handler);
        var logger = new Mock<ILogger<McpServerHealthCheck>>();

        var check = new McpServerHealthCheck(factory.Object, logger.Object);
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task McpServerHealthCheck_ReturnsDegraded_WhenServerReturns500()
    {
        DelegatingHandler handler = CreateMockHandler(HttpStatusCode.InternalServerError);
        Mock<IHttpClientFactory> factory = CreateFactory(handler);
        var logger = new Mock<ILogger<McpServerHealthCheck>>();

        var check = new McpServerHealthCheck(factory.Object, logger.Object);
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task McpServerHealthCheck_ReturnsUnhealthy_WhenExceptionThrown()
    {
        DelegatingHandler handler = CreateThrowingHandler(new HttpRequestException("Connection refused"));
        Mock<IHttpClientFactory> factory = CreateFactory(handler);
        var logger = new Mock<ILogger<McpServerHealthCheck>>();

        var check = new McpServerHealthCheck(factory.Object, logger.Object);
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeOfType<HttpRequestException>();
    }

    #endregion

    #region AzureOpenAiHealthCheck

    [Fact]
    public async Task AzureOpenAiHealthCheck_ReturnsHealthy_WhenEndpointResponds200()
    {
        DelegatingHandler handler = CreateMockHandler(HttpStatusCode.OK);
        Mock<IHttpClientFactory> factory = CreateRawFactory(handler);
        IConfiguration config = CreateConfig("https://test.openai.azure.com", "test-key");
        var logger = new Mock<ILogger<AzureOpenAiHealthCheck>>();

        var check = new AzureOpenAiHealthCheck(factory.Object, config, logger.Object);
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task AzureOpenAiHealthCheck_ReturnsDegraded_WhenUnauthorized()
    {
        DelegatingHandler handler = CreateMockHandler(HttpStatusCode.Unauthorized);
        Mock<IHttpClientFactory> factory = CreateRawFactory(handler);
        IConfiguration config = CreateConfig("https://test.openai.azure.com", "bad-key");
        var logger = new Mock<ILogger<AzureOpenAiHealthCheck>>();

        var check = new AzureOpenAiHealthCheck(factory.Object, config, logger.Object);
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task AzureOpenAiHealthCheck_ReturnsDegraded_WhenEndpointNotConfigured()
    {
        DelegatingHandler handler = CreateMockHandler(HttpStatusCode.OK);
        Mock<IHttpClientFactory> factory = CreateRawFactory(handler);
        IConfiguration config = CreateConfig(null, null);
        var logger = new Mock<ILogger<AzureOpenAiHealthCheck>>();

        var check = new AzureOpenAiHealthCheck(factory.Object, config, logger.Object);
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task AzureOpenAiHealthCheck_ReturnsUnhealthy_WhenExceptionThrown()
    {
        DelegatingHandler handler = CreateThrowingHandler(new HttpRequestException("DNS resolution failed"));
        Mock<IHttpClientFactory> factory = CreateRawFactory(handler);
        IConfiguration config = CreateConfig("https://test.openai.azure.com", "key");
        var logger = new Mock<ILogger<AzureOpenAiHealthCheck>>();

        var check = new AzureOpenAiHealthCheck(factory.Object, config, logger.Object);
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task AzureOpenAiHealthCheck_UsesApimSubscriptionKeyHeader_WhenConfigured()
    {
        HttpRequestMessage? capturedRequest = null;
        DelegatingHandler handler = CreateCapturingHandler(HttpStatusCode.OK, request => capturedRequest = request);
        Mock<IHttpClientFactory> factory = CreateRawFactory(handler);
        IConfiguration config = CreateConfig(
            endpoint: "https://test.azure-api.net/inference/openai",
            apiKey: "direct-key",
            apimSubscriptionKey: "apim-sub-key",
            useManagedIdentity: false);
        var logger = new Mock<ILogger<AzureOpenAiHealthCheck>>();

        var check = new AzureOpenAiHealthCheck(factory.Object, config, logger.Object);
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        capturedRequest.Should().NotBeNull();
        HttpRequestMessage request = capturedRequest ?? throw new InvalidOperationException("Expected the health check to issue a request.");
        request.Headers.TryGetValues("api-key", out IEnumerable<string>? headerValues).Should().BeTrue();
        headerValues.Should().ContainSingle().Which.Should().Be("apim-sub-key");
    }

    [Fact]
    public async Task AzureOpenAiHealthCheck_DoesNotSendApiKeyHeader_WhenManagedIdentityEnabled()
    {
        HttpRequestMessage? capturedRequest = null;
        DelegatingHandler handler = CreateCapturingHandler(HttpStatusCode.OK, request => capturedRequest = request);
        Mock<IHttpClientFactory> factory = CreateRawFactory(handler);
        IConfiguration config = CreateConfig(
            endpoint: "https://test.openai.azure.com",
            apiKey: "direct-key",
            apimSubscriptionKey: "apim-sub-key",
            useManagedIdentity: true);
        var logger = new Mock<ILogger<AzureOpenAiHealthCheck>>();

        var check = new AzureOpenAiHealthCheck(factory.Object, config, logger.Object);
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        capturedRequest.Should().NotBeNull();
        HttpRequestMessage request = capturedRequest ?? throw new InvalidOperationException("Expected the health check to issue a request.");
        request.Headers.Contains("api-key").Should().BeFalse();
    }

    [Fact]
    public async Task AzureOpenAiHealthCheck_ProbesGatewayModelsRoute_IncludingOpenAiSegmentAndApiVersion()
    {
        HttpRequestMessage? capturedRequest = null;
        DelegatingHandler handler = CreateCapturingHandler(HttpStatusCode.OK, request => capturedRequest = request);
        Mock<IHttpClientFactory> factory = CreateRawFactory(handler);
        IConfiguration config = CreateConfig(
            endpoint: "https://test.azure-api.net/inference",
            apiKey: null,
            apimSubscriptionKey: "apim-sub-key",
            useManagedIdentity: false,
            apiVersion: "2025-03-01-preview");
        var logger = new Mock<ILogger<AzureOpenAiHealthCheck>>();

        var check = new AzureOpenAiHealthCheck(factory.Object, config, logger.Object);
        await check.CheckHealthAsync(new HealthCheckContext());

        HttpRequestMessage request = capturedRequest ?? throw new InvalidOperationException("Expected the health check to issue a request.");

        // The APIM inference API is registered at path "{inference}/openai", so a probe
        // of "{endpoint}/models" matches no API and 404s on every gateway deployment.
        request.RequestUri!.AbsoluteUri.Should()
            .Be("https://test.azure-api.net/inference/openai/models?api-version=2025-03-01-preview");
    }

    [Theory]
    // Gateway base — the SDK's own "/openai" segment must be appended.
    [InlineData("https://test.azure-api.net/inference", "https://test.azure-api.net/inference/openai/models?api-version=v1")]
    // Trailing slash must not produce a doubled separator.
    [InlineData("https://test.azure-api.net/inference/", "https://test.azure-api.net/inference/openai/models?api-version=v1")]
    // Direct Azure OpenAI account (Development / AllowDirectEndpoint).
    [InlineData("https://test.cognitiveservices.azure.com", "https://test.cognitiveservices.azure.com/openai/models?api-version=v1")]
    // Defensive: an endpoint that already carries "/openai" must not be doubled.
    [InlineData("https://test.azure-api.net/inference/openai", "https://test.azure-api.net/inference/openai/models?api-version=v1")]
    public void BuildModelsProbeUrl_ComposesTheSdkRouteShape(string endpoint, string expected) =>
        AzureOpenAiHealthCheck.BuildModelsProbeUrl(endpoint, "v1").Should().Be(expected);

    [Fact]
    public async Task AzureOpenAiHealthCheck_ReturnsDegradedNamingTheRoute_WhenProbeReturns404()
    {
        DelegatingHandler handler = CreateMockHandler(HttpStatusCode.NotFound);
        Mock<IHttpClientFactory> factory = CreateRawFactory(handler);
        IConfiguration config = CreateConfig("https://test.azure-api.net/inference", "key");
        var logger = new Mock<ILogger<AzureOpenAiHealthCheck>>();

        var check = new AzureOpenAiHealthCheck(factory.Object, config, logger.Object);
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("404").And.Contain("model-listing route");
    }

    [Fact]
    public async Task AzureOpenAiHealthCheck_FallsBackToADefaultApiVersion_WhenNotConfigured()
    {
        HttpRequestMessage? capturedRequest = null;
        DelegatingHandler handler = CreateCapturingHandler(HttpStatusCode.OK, request => capturedRequest = request);
        Mock<IHttpClientFactory> factory = CreateRawFactory(handler);
        IConfiguration config = CreateConfig("https://test.azure-api.net/inference", "key");
        var logger = new Mock<ILogger<AzureOpenAiHealthCheck>>();

        var check = new AzureOpenAiHealthCheck(factory.Object, config, logger.Object);
        await check.CheckHealthAsync(new HealthCheckContext());

        HttpRequestMessage request = capturedRequest ?? throw new InvalidOperationException("Expected the health check to issue a request.");
        request.RequestUri!.Query.Should().StartWith("?api-version=").And.NotBe("?api-version=");
    }

    #endregion

    #region Helpers

    private static DelegatingHandler CreateMockHandler(HttpStatusCode statusCode)
    {
        var handler = new Mock<DelegatingHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode));
        handler.Object.InnerHandler = new HttpClientHandler();
        return handler.Object;
    }

    private static DelegatingHandler CreateCapturingHandler(HttpStatusCode statusCode, Action<HttpRequestMessage> onRequest) =>
        new CallbackHandler(onRequest, () => new HttpResponseMessage(statusCode));

    private static DelegatingHandler CreateThrowingHandler(Exception ex)
    {
        var handler = new Mock<DelegatingHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(ex);
        handler.Object.InnerHandler = new HttpClientHandler();
        return handler.Object;
    }

    private static Mock<IHttpClientFactory> CreateFactory(DelegatingHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("McpServer"))
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5050") });
        return factory;
    }

    private static Mock<IHttpClientFactory> CreateRawFactory(DelegatingHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler));
        return factory;
    }

    private static IConfiguration CreateConfig(
        string? endpoint,
        string? apiKey,
        string? apimSubscriptionKey = null,
        bool? useManagedIdentity = null,
        string? apiVersion = null)
    {
        var configData = new Dictionary<string, string?>();
        if (endpoint != null) configData["OpenAI:Endpoint"] = endpoint;
        if (apiKey != null) configData["OpenAI:ApiKey"] = apiKey;
        if (apimSubscriptionKey != null) configData["OpenAI:ApimSubscriptionKey"] = apimSubscriptionKey;
        if (useManagedIdentity.HasValue) configData["OpenAI:UseManagedIdentity"] = useManagedIdentity.Value.ToString();
        if (apiVersion != null) configData["OpenAI:ApiVersion"] = apiVersion;

        return new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();
    }

    private sealed class CallbackHandler(Action<HttpRequestMessage> onRequest, Func<HttpResponseMessage> responseFactory) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest(request);
            return Task.FromResult(responseFactory());
        }
    }

    #endregion
}

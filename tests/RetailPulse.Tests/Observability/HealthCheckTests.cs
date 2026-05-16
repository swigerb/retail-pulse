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

    private static IConfiguration CreateConfig(string? endpoint, string? apiKey)
    {
        var configData = new Dictionary<string, string?>();
        if (endpoint != null) configData["OpenAI:Endpoint"] = endpoint;
        if (apiKey != null) configData["OpenAI:ApiKey"] = apiKey;

        return new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();
    }

    #endregion
}

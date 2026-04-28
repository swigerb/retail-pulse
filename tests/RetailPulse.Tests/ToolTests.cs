using System.Net;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Moq.Protected;
using RetailPulse.Api.Tools;

namespace RetailPulse.Tests;

public class ToolTests
{
    private static HttpClient CreateMockHttpClient(HttpStatusCode statusCode, string content)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content)
            });

        return new HttpClient(handler.Object) { BaseAddress = new Uri("http://localhost:5000") };
    }

    private static HttpClient CreateFailingHttpClient()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        return new HttpClient(handler.Object) { BaseAddress = new Uri("http://localhost:5000") };
    }

    [Fact]
    public async Task DepletionStatsTool_WhenServerReachable_ReturnsData()
    {
        var expectedResponse = JsonSerializer.Serialize(new
        {
            brand = "Patrón Silver",
            region = "Florida",
            period = "YTD",
            metrics = new { depletions_yoy = "+2.1%", status = "On Track" }
        });

        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, expectedResponse);
        var tool = new DepletionStatsTool(httpClient);

        var result = await tool.GetDepletionStats("Patrón Silver", "Florida", "YTD");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("brand").GetString().Should().Be("Patrón Silver");
        doc.RootElement.GetProperty("region").GetString().Should().Be("Florida");
    }

    [Fact]
    public async Task DepletionStatsTool_WhenServerUnreachable_ReturnsFallback()
    {
        var httpClient = CreateFailingHttpClient();
        var tool = new DepletionStatsTool(httpClient);

        var result = await tool.GetDepletionStats("Patrón Silver", "Florida", "YTD");

        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("brand").GetString().Should().Be("Patrón Silver");
        root.GetProperty("region").GetString().Should().Be("Florida");
        root.GetProperty("sentiment_summary").GetString().Should().Contain("MCP server not reachable");
    }

    [Fact]
    public async Task FieldSentimentTool_WhenServerReachable_ReturnsData()
    {
        var expectedResponse = JsonSerializer.Serialize(new
        {
            brand = "Angel's Envy",
            region = "New York",
            sentiment = "Strong pull in Manhattan"
        });

        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, expectedResponse);
        var tool = new FieldSentimentTool(httpClient);

        var result = await tool.GetFieldSentiment("Angel's Envy", "New York");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("brand").GetString().Should().Be("Angel's Envy");
        doc.RootElement.GetProperty("region").GetString().Should().Be("New York");
    }

    [Fact]
    public async Task FieldSentimentTool_WhenServerUnreachable_ReturnsFallback()
    {
        var httpClient = CreateFailingHttpClient();
        var tool = new FieldSentimentTool(httpClient);

        var result = await tool.GetFieldSentiment("Angel's Envy", "New York");

        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("brand").GetString().Should().Be("Angel's Envy");
        root.GetProperty("region").GetString().Should().Be("New York");
        root.GetProperty("sentiment").GetString().Should().Contain("MCP server not reachable");
        root.GetProperty("source").GetString().Should().Be("fallback");
    }

    [Fact]
    public async Task DepletionStatsTool_WhenServerReturns500_ReturnsFallback()
    {
        var httpClient = CreateMockHttpClient(HttpStatusCode.InternalServerError, "Server Error");
        var tool = new DepletionStatsTool(httpClient);

        var result = await tool.GetDepletionStats("Grey Goose", "National", "Q1");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("sentiment_summary").GetString()
            .Should().Contain("MCP server not reachable");
    }
}

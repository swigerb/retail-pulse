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

    // -------------------------- DepletionStatsTool --------------------------

    [Fact]
    public async Task DepletionStatsTool_WhenServerReachable_ReturnsData()
    {
        var expectedResponse = JsonSerializer.Serialize(new
        {
            brand = "Sierra Gold Tequila",
            region = "Northeast",
            period = "YTD",
            metrics = new { depletions_yoy = "+2.1%", status = "On Track" }
        });

        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, expectedResponse);
        var tool = new DepletionStatsTool(httpClient);

        var result = await tool.GetDepletionStats("Sierra Gold Tequila", "Northeast", "YTD");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("brand").GetString().Should().Be("Sierra Gold Tequila");
        doc.RootElement.GetProperty("region").GetString().Should().Be("Northeast");
    }

    [Fact]
    public async Task DepletionStatsTool_WhenServerUnreachable_ReturnsFallback()
    {
        var httpClient = CreateFailingHttpClient();
        var tool = new DepletionStatsTool(httpClient);

        var result = await tool.GetDepletionStats("Sierra Gold Tequila", "Northeast", "YTD");

        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("brand").GetString().Should().Be("Sierra Gold Tequila");
        root.GetProperty("region").GetString().Should().Be("Northeast");
        root.GetProperty("sentiment_summary").GetString().Should().Contain("MCP server not reachable");
    }

    [Fact]
    public async Task DepletionStatsTool_WhenServerReturns500_ReturnsFallback()
    {
        var httpClient = CreateMockHttpClient(HttpStatusCode.InternalServerError, "Server Error");
        var tool = new DepletionStatsTool(httpClient);

        var result = await tool.GetDepletionStats("Summit Vodka", "National", "Q1");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("sentiment_summary").GetString()
            .Should().Contain("MCP server not reachable");
    }

    // -------------------------- FieldSentimentTool --------------------------

    [Fact]
    public async Task FieldSentimentTool_WhenServerReachable_ReturnsData()
    {
        var expectedResponse = JsonSerializer.Serialize(new
        {
            brand = "Ridgeline Bourbon",
            region = "Northeast",
            sentiment = "Strong pull in Manhattan"
        });

        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, expectedResponse);
        var tool = new FieldSentimentTool(httpClient);

        var result = await tool.GetFieldSentiment("Ridgeline Bourbon", "Northeast");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("brand").GetString().Should().Be("Ridgeline Bourbon");
        doc.RootElement.GetProperty("region").GetString().Should().Be("Northeast");
    }

    [Fact]
    public async Task FieldSentimentTool_WhenServerUnreachable_ReturnsFallback()
    {
        var httpClient = CreateFailingHttpClient();
        var tool = new FieldSentimentTool(httpClient);

        var result = await tool.GetFieldSentiment("Ridgeline Bourbon", "Northeast");

        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("brand").GetString().Should().Be("Ridgeline Bourbon");
        root.GetProperty("region").GetString().Should().Be("Northeast");
        root.GetProperty("sentiment").GetString().Should().Contain("MCP server not reachable");
        root.GetProperty("source").GetString().Should().Be("fallback");
    }

    // -------------------------- ShipmentStatsTool --------------------------

    [Fact]
    public async Task ShipmentStatsTool_WhenServerReachable_ReturnsData()
    {
        var expectedResponse = JsonSerializer.Serialize(new
        {
            brand = "Sierra Gold Tequila",
            region = "Northeast",
            period = "YTD",
            shipments = new
            {
                shipments_yoy = "+5.0%",
                cases_shipped = 1200,
                cases_depleted = 1100
            },
            anomaly = new { type = "healthy", risk_level = "low" }
        });

        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, expectedResponse);
        var tool = new ShipmentStatsTool(httpClient);

        var result = await tool.GetShipmentStats("Sierra Gold Tequila", "Northeast", "YTD");

        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("brand").GetString().Should().Be("Sierra Gold Tequila");
        root.GetProperty("region").GetString().Should().Be("Northeast");
        root.GetProperty("shipments").GetProperty("cases_shipped").GetInt32().Should().Be(1200);
        root.GetProperty("anomaly").GetProperty("type").GetString().Should().Be("healthy");
    }

    [Fact]
    public async Task ShipmentStatsTool_WhenServerUnreachable_ReturnsFallback()
    {
        var httpClient = CreateFailingHttpClient();
        var tool = new ShipmentStatsTool(httpClient);

        var result = await tool.GetShipmentStats("Sierra Gold Tequila", "Northeast", "YTD");

        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("brand").GetString().Should().Be("Sierra Gold Tequila");
        root.GetProperty("region").GetString().Should().Be("Northeast");
        root.GetProperty("period").GetString().Should().Be("YTD");
        root.GetProperty("error").GetString().Should().Contain("MCP server not reachable");
        root.GetProperty("source").GetString().Should().Be("fallback");
    }

    [Fact]
    public async Task ShipmentStatsTool_WhenServerReturns500_ReturnsFallback()
    {
        var httpClient = CreateMockHttpClient(HttpStatusCode.InternalServerError, "Server Error");
        var tool = new ShipmentStatsTool(httpClient);

        var result = await tool.GetShipmentStats("Summit Vodka", "West Coast", "Q2");

        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("source").GetString().Should().Be("fallback");
        root.GetProperty("error").GetString().Should().Contain("MCP server not reachable");
    }

    [Fact]
    public async Task ShipmentStatsTool_DefaultPeriod_IsYTD()
    {
        // Capture the request URI to verify the default value
        Uri? capturedUri = null;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUri = req.RequestUri)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{}")
            });

        var client = new HttpClient(handler.Object) { BaseAddress = new Uri("http://localhost:5000") };
        var tool = new ShipmentStatsTool(client);

        await tool.GetShipmentStats("Sierra Gold Tequila", "Northeast");

        capturedUri.Should().NotBeNull();
        capturedUri!.Query.Should().Contain("period=YTD",
            "ShipmentStatsTool defaults the period parameter to YTD when none is supplied");
    }

    [Fact]
    public async Task ShipmentStatsTool_EncodesSpecialCharactersInQueryString()
    {
        Uri? capturedUri = null;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUri = req.RequestUri)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{}")
            });

        var client = new HttpClient(handler.Object) { BaseAddress = new Uri("http://localhost:5000") };
        var tool = new ShipmentStatsTool(client);

        await tool.GetShipmentStats("Brand & Co", "Region/Sub", "Q1");

        capturedUri.Should().NotBeNull();
        capturedUri!.Query.Should().Contain("Brand%20%26%20Co");
        capturedUri.Query.Should().Contain("Region%2FSub");
    }
}

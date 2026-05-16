using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Tools;

namespace RetailPulse.Tests.Chaos;

/// <summary>
/// Simulates Azure OpenAI 429 throttling responses and verifies tools degrade gracefully.
/// </summary>
public class AzureOpenAiThrottleChaosTests
{
    [Fact]
    public async Task Tool_Receives429_ReturnsFallbackGracefully()
    {
        var handler = new ThrottlingHttpMessageHandler(HttpStatusCode.TooManyRequests);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5200") };
        var tool = new FieldSentimentTool(client, NullLogger<FieldSentimentTool>.Instance);

        string result = await tool.GetFieldSentiment("TestBrand", "Florida");

        result.Should().Contain("fallback");
        result.Should().Contain("TestBrand");
    }

    [Fact]
    public async Task Tool_Receives503_ReturnsFallbackGracefully()
    {
        var handler = new ThrottlingHttpMessageHandler(HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5200") };
        var tool = new DepletionStatsTool(client, NullLogger<DepletionStatsTool>.Instance);

        string result = await tool.GetDepletionStats("TestBrand", "Texas", "Q1");

        result.Should().Contain("MCP server not reachable");
    }

    [Fact]
    public async Task Tool_ReceivesMultiple429s_AllReturnFallback()
    {
        var handler = new ThrottlingHttpMessageHandler(HttpStatusCode.TooManyRequests);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5200") };
        var tool = new MarketShareTool(client, NullLogger<MarketShareTool>.Instance);

        // Simulate burst of requests that all get throttled
        IEnumerable<Task<string>> tasks = Enumerable.Range(0, 10)
            .Select(_ => tool.GetMarketShare(brand: "TestBrand"));

        string[] results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r => r.Should().Contain("MCP server not reachable"));
    }

    [Fact]
    public async Task Tool_Receives500_ReturnsFallback()
    {
        var handler = new ThrottlingHttpMessageHandler(HttpStatusCode.InternalServerError);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5200") };
        var tool = new CompetitorPricingTool(client, NullLogger<CompetitorPricingTool>.Instance);

        string result = await tool.GetCompetitorPricing(brand: "TestBrand");

        result.Should().Contain("MCP server not reachable");
    }

    private sealed class ThrottlingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public ThrottlingHttpMessageHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent($"{{\"error\":\"throttled\",\"status\":{(int)_statusCode}}}")
            };

            if (_statusCode == HttpStatusCode.TooManyRequests)
            {
                response.Headers.Add("Retry-After", "30");
            }

            return Task.FromResult(response);
        }
    }
}

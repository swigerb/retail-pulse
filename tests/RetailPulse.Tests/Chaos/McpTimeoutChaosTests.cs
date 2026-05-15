using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Tools;

namespace RetailPulse.Tests.Chaos;

/// <summary>
/// Simulates MCP server timeouts and verifies graceful degradation.
/// </summary>
public class McpTimeoutChaosTests
{
    private static HttpClient CreateTimeoutClient(TimeSpan delay)
    {
        var handler = new DelayedHttpMessageHandler(delay);
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5200") };
    }

    [Fact]
    public async Task FieldSentimentTool_McpTimeout_ReturnsFallback()
    {
        // Simulate a 10s delay which will exceed a typical timeout
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var client = CreateTimeoutClient(TimeSpan.FromSeconds(30));
        var tool = new FieldSentimentTool(client, NullLogger<FieldSentimentTool>.Instance);

        var result = await tool.GetFieldSentiment("TestBrand", "TestRegion", cts.Token);

        result.Should().Contain("fallback");
        result.Should().Contain("TestBrand");
    }

    [Fact]
    public async Task DepletionStatsTool_McpTimeout_ReturnsFallback()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var client = CreateTimeoutClient(TimeSpan.FromSeconds(30));
        var tool = new DepletionStatsTool(client, NullLogger<DepletionStatsTool>.Instance);

        var result = await tool.GetDepletionStats("TestBrand", "TestRegion", "YTD", cts.Token);

        result.Should().Contain("MCP server not reachable");
        result.Should().Contain("TestBrand");
    }

    [Fact]
    public async Task ShipmentStatsTool_McpTimeout_ReturnsFallback()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var client = CreateTimeoutClient(TimeSpan.FromSeconds(30));
        var tool = new ShipmentStatsTool(client, NullLogger<ShipmentStatsTool>.Instance);

        var result = await tool.GetShipmentStats("TestBrand", "TestRegion", "YTD", cts.Token);

        result.Should().Contain("MCP server not reachable");
        result.Should().Contain("TestBrand");
    }

    [Fact]
    public async Task CompetitiveLandscapeTool_McpTimeout_ReturnsFallback()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var client = CreateTimeoutClient(TimeSpan.FromSeconds(30));
        var tool = new CompetitiveLandscapeTool(client, NullLogger<CompetitiveLandscapeTool>.Instance);

        var result = await tool.GetCompetitiveLandscape("Spirits", "Northeast", cts.Token);

        result.Should().Contain("MCP server not reachable");
    }

    /// <summary>
    /// Handler that delays responses to simulate a slow/unreachable server.
    /// </summary>
    private sealed class DelayedHttpMessageHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;

        public DelayedHttpMessageHandler(TimeSpan delay) => _delay = delay;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

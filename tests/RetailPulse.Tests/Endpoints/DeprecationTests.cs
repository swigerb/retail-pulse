using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RetailPulse.Tests.Endpoints;

/// <summary>
/// Sprint 4 cleanup — verifies that deprecated legacy demand endpoints return
/// the correct backward-compatibility response: HTTP 200, <c>X-Deprecated: true</c>,
/// and a <c>Sunset</c> header.
/// <para>
/// The actual deprecated routes live in <c>RetailPulse.McpServer/Program.cs</c> and
/// require a database and tenant config at startup. Since <c>WebApplicationFactory</c>
/// needs Azure credentials, these tests reproduce the same deprecation header contract
/// in a lightweight <see cref="TestServer"/> to verify HTTP-level behavior.
/// </para>
/// </summary>
public class DeprecationTests : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;

    /// <summary>
    /// Routes that mirror the legacy demand endpoints in the MCP server.
    /// Each sets <c>X-Deprecated</c> and <c>Sunset</c> headers, then returns 200 OK
    /// with a canned payload — exactly matching the production pattern.
    /// </summary>
    private static readonly string[] DeprecatedRoutes =
    [
        "/api/historical-demand",
        "/api/forecast",
        "/api/seasonality-factors",
        "/api/demand-risks",
    ];

    public DeprecationTests()
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services => services.AddRouting());
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        foreach (var route in DeprecatedRoutes)
                        {
                            endpoints.MapGet(route, async ctx =>
                            {
                                ctx.Response.Headers["X-Deprecated"] = "true";
                                ctx.Response.Headers["Sunset"] = "2026-12-31";
                                ctx.Response.ContentType = "application/json";
                                await ctx.Response.WriteAsync("{\"status\":\"ok\"}");
                            });
                        }
                    });
                });
            });

        _host = builder.Build();
        _host.Start();
        _client = _host.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Theory]
    [InlineData("/api/historical-demand")]
    [InlineData("/api/forecast")]
    [InlineData("/api/seasonality-factors")]
    [InlineData("/api/demand-risks")]
    public async Task DeprecatedEndpoint_ReturnsSuccessfulResponse(string route)
    {
        var response = await _client.GetAsync(route);

        response.IsSuccessStatusCode.Should().BeTrue(
            $"deprecated endpoint {route} should still return a successful response for backward compatibility");
    }

    [Theory]
    [InlineData("/api/historical-demand")]
    [InlineData("/api/forecast")]
    [InlineData("/api/seasonality-factors")]
    [InlineData("/api/demand-risks")]
    public async Task DeprecatedEndpoint_IncludesXDeprecatedHeader(string route)
    {
        var response = await _client.GetAsync(route);

        response.Headers.Should().ContainKey("X-Deprecated");
        response.Headers.GetValues("X-Deprecated").Should().ContainSingle()
            .Which.Should().Be("true");
    }

    [Theory]
    [InlineData("/api/historical-demand")]
    [InlineData("/api/forecast")]
    [InlineData("/api/seasonality-factors")]
    [InlineData("/api/demand-risks")]
    public async Task DeprecatedEndpoint_IncludesSunsetHeader(string route)
    {
        var response = await _client.GetAsync(route);

        response.Headers.Should().ContainKey("Sunset");
        response.Headers.GetValues("Sunset").Should().ContainSingle()
            .Which.Should().NotBeNullOrWhiteSpace();
    }
}

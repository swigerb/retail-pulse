using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using RetailPulse.Api.Middleware;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Tests verifying the SecurityHeadersMiddleware adds all required security headers.
/// </summary>
public class SecurityHeadersMiddlewareTests
{
    [Fact]
    public async Task AllSecurityHeaders_ArePresentInResponse()
    {
        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder.UseTestServer()
                    .Configure(app =>
                    {
                        app.UseMiddleware<SecurityHeadersMiddleware>();
                        app.Run(async context => await context.Response.WriteAsync("OK"));
                    }))
            .StartAsync();

        HttpClient client = host.GetTestClient();
        HttpResponseMessage response = await client.GetAsync("/any-path");

        // X-Content-Type-Options
        response.Headers.GetValues("X-Content-Type-Options")
            .Should().Contain("nosniff");

        // X-Frame-Options
        response.Headers.GetValues("X-Frame-Options")
            .Should().Contain("DENY");

        // Referrer-Policy
        response.Headers.GetValues("Referrer-Policy")
            .Should().Contain("strict-origin-when-cross-origin");

        // Permissions-Policy
        response.Headers.GetValues("Permissions-Policy")
            .Should().Contain("camera=(), microphone=(), geolocation=()");

        // Content-Security-Policy
        string csp = response.Headers.GetValues("Content-Security-Policy").Single();
        csp.Should().Contain("default-src 'self'");
        csp.Should().Contain("style-src 'self' 'unsafe-inline'");
        csp.Should().Contain("script-src 'self'");
        csp.Should().Contain("font-src 'self'");
    }

    [Fact]
    public async Task HSTS_NotSentOnHttp()
    {
        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder.UseTestServer()
                    .Configure(app =>
                    {
                        app.UseMiddleware<SecurityHeadersMiddleware>();
                        app.Run(async context => await context.Response.WriteAsync("OK"));
                    }))
            .StartAsync();

        HttpClient client = host.GetTestClient();
        HttpResponseMessage response = await client.GetAsync("http://localhost/test");

        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse();
    }

    [Fact]
    public async Task ExistingHeaders_AreNotOverwritten()
    {
        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder.UseTestServer()
                    .Configure(app =>
                    {
                        app.UseMiddleware<SecurityHeadersMiddleware>();
                        app.Run(async context =>
                        {
                            context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
                            await context.Response.WriteAsync("OK");
                        });
                    }))
            .StartAsync();

        HttpClient client = host.GetTestClient();
        HttpResponseMessage response = await client.GetAsync("/");

        // TryAdd should preserve the existing header
        response.Headers.GetValues("X-Frame-Options")
            .Should().Contain("SAMEORIGIN");
    }
}

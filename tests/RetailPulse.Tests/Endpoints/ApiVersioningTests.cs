using System.Net;
using Asp.Versioning;
using Asp.Versioning.Builder;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// The {version:apiVersion} route token is consumed by Asp.Versioning middleware,
// not by the minimal-API handler. ASP0018 would otherwise fire on every handler.
#pragma warning disable ASP0018

namespace RetailPulse.Tests.Endpoints;

/// <summary>
/// Contract tests for <c>Asp.Versioning.Http</c>. These exist to guard the
/// 8.x → 10.x upgrade documented in <c>.squad/decisions.md</c> — they
/// reproduce the EXACT same <see cref="ApiVersioningOptions"/> configured
/// in <c>RetailPulse.Api/Program.cs</c> (default v1.0, URL segment reader,
/// <c>AssumeDefaultVersionWhenUnspecified=true</c>, <c>ReportApiVersions=true</c>)
/// and assert that routing, default-version fallback, error responses, and
/// the <c>api-supported-versions</c> response header all behave correctly.
/// <para>
/// Production endpoints in this project are currently unversioned at the
/// route level (no <c>/api/v{version}/</c> segments), so the suite spins up
/// a minimal <see cref="TestServer"/> with versioned endpoints attached to
/// an <see cref="ApiVersionSet"/> — the same building block any future
/// versioned route would use. If the upgrade subtly breaks
/// <c>ApiVersionSetBuilder</c>, the URL segment reader, or the error-response
/// pipeline, one of these assertions will fail.
/// </para>
/// </summary>
public class ApiVersioningTests : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;

    public ApiVersioningTests()
    {
        IHostBuilder builder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddRouting();
                    // Mirror Program.cs lines 188-195 exactly.
                    services.AddApiVersioning(options =>
                    {
                        options.DefaultApiVersion = new ApiVersion(1, 0);
                        options.AssumeDefaultVersionWhenUnspecified = true;
                        options.ReportApiVersions = true;
                        options.ApiVersionReader = new UrlSegmentApiVersionReader();
                    });
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        ApiVersionSet versionSet = endpoints.NewApiVersionSet()
                            .HasApiVersion(new ApiVersion(1, 0))
                            .HasApiVersion(new ApiVersion(2, 0))
                            .ReportApiVersions()
                            .Build();

                        // Versioned mirrors of the three "key" endpoints called
                        // out in the task: chat, health, tenant config.
                        endpoints.MapGet("/api/v{version:apiVersion}/health",
                                static () => Results.Ok(new { status = "ok", version = "v1-or-v2" }))
                            .WithApiVersionSet(versionSet)
                            .MapToApiVersion(new ApiVersion(1, 0))
                            .MapToApiVersion(new ApiVersion(2, 0));

                        endpoints.MapPost("/api/v{version:apiVersion}/chat",
                                static (HttpContext ctx) => Results.Ok(new { reply = "v1-chat" }))
                            .WithApiVersionSet(versionSet)
                            .MapToApiVersion(new ApiVersion(1, 0));

                        endpoints.MapPost("/api/v{version:apiVersion}/chat",
                                static (HttpContext ctx) => Results.Ok(new { reply = "v2-chat" }))
                            .WithApiVersionSet(versionSet)
                            .MapToApiVersion(new ApiVersion(2, 0));

                        endpoints.MapGet("/api/v{version:apiVersion}/tenant/config",
                                static () => Results.Ok(new { tenant = "test", version = 1 }))
                            .WithApiVersionSet(versionSet)
                            .MapToApiVersion(new ApiVersion(1, 0));

                        endpoints.MapGet("/api/v{version:apiVersion}/tenant/config",
                                static () => Results.Ok(new { tenant = "test", version = 2, extra = "v2-only" }))
                            .WithApiVersionSet(versionSet)
                            .MapToApiVersion(new ApiVersion(2, 0));
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
        GC.SuppressFinalize(this);
    }

    // ── 1. Version routing via URL segment ─────────────────────────────────

    [Theory]
    [InlineData("/api/v1/health")]
    [InlineData("/api/v2/health")]
    public async Task UrlSegment_ExplicitVersion_RoutesToCorrectEndpoint(string url)
    {
        HttpResponseMessage response = await _client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"URL segment versioning should route {url} to a registered handler");
    }

    [Fact]
    public async Task UrlSegment_V1Chat_ReturnsV1Payload()
    {
        HttpResponseMessage response = await _client.PostAsync("/api/v1/chat", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("v1-chat", "v1 route must hit the v1 handler, not v2");
    }

    [Fact]
    public async Task UrlSegment_V2Chat_ReturnsV2Payload()
    {
        HttpResponseMessage response = await _client.PostAsync("/api/v2/chat", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("v2-chat", "v2 route must hit the v2 handler, not v1");
    }

    [Fact]
    public async Task UrlSegment_V2TenantConfig_ReturnsV2OnlyField()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/v2/tenant/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("v2-only", "v2 tenant config should expose its v2-specific field");
    }

    // ── 2. Default version when version segment is absent ──────────────────
    //
    // With UrlSegmentApiVersionReader the version MUST come from the URL —
    // ambient default-version assumption cannot synthesize a missing segment
    // into a route match. A request to /api/health (no v{n}) therefore 404s
    // because no route template matches. This is the expected and documented
    // behavior on both 8.x and 10.x.

    [Fact]
    public async Task NoVersionSegment_Returns404_BecauseUrlSegmentReaderRequiresIt()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/health");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "url segment reader cannot route a request that has no version segment in the path");
    }

    // ── 3. Unsupported version returns a proper error ──────────────────────
    //
    // Because the project uses UrlSegmentApiVersionReader, the version is
    // baked into the route template. An unknown version (v99) doesn't match
    // any registered route, so the framework returns 404 — not the 400 that
    // header/query-string readers produce. This is documented, stable
    // behavior across Asp.Versioning 6.x → 10.x and is what the upgrade
    // must continue to honor.

    [Fact]
    public async Task UrlSegment_UnsupportedVersion_IsRejected()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/v99/health");

        response.StatusCode.Should().BeOneOf(
            [HttpStatusCode.NotFound, HttpStatusCode.BadRequest],
            "url-segment versioning rejects an unsupported version as either 404 (no route match) or 400 (versioning middleware) — never as a successful 2xx");
    }

    [Fact]
    public async Task UrlSegment_UnsupportedVersion_DoesNotReachAnyHandler()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/v99/health");

        ((int)response.StatusCode).Should().BeGreaterThanOrEqualTo(400,
            "unsupported API version must not return any handler payload");

        if (response.Content.Headers.ContentLength is > 0)
        {
            string body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain("v1-or-v2",
                "the handler payload must not leak when the requested version is unsupported");
        }
    }

    // ── 4. Version discovery: api-supported-versions response header ──────

    [Fact]
    public async Task ReportApiVersions_AddsSupportedVersionsHeader()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/v1/health");

        response.Headers.Should().ContainKey("api-supported-versions",
            "ReportApiVersions=true must surface the api-supported-versions response header");
        string headerValue = string.Join(",", response.Headers.GetValues("api-supported-versions"));
        headerValue.Should().Contain("1.0").And.Contain("2.0",
            "header must enumerate every version registered on the version set");
    }

    [Fact]
    public async Task ReportApiVersions_HeaderIsPresentOnV2Response()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/v2/health");

        response.Headers.GetValues("api-supported-versions").Should().NotBeEmpty();
    }

    // ── 5. DI / option-shape smoke test ────────────────────────────────────
    //
    // The upgrade from 8.x → 10.x is the single most likely place for the
    // options object's property surface to shift. Resolving the configured
    // options out of DI proves the names we use in Program.cs still exist.

    [Fact]
    public void ApiVersioningOptions_ResolvedFromDi_MatchesProgramConfiguration()
    {
        Microsoft.Extensions.Options.IOptions<ApiVersioningOptions> options =
            _host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiVersioningOptions>>();

        options.Value.DefaultApiVersion.Should().Be(new ApiVersion(1, 0));
        options.Value.AssumeDefaultVersionWhenUnspecified.Should().BeTrue();
        options.Value.ReportApiVersions.Should().BeTrue();
        options.Value.ApiVersionReader.Should().BeOfType<UrlSegmentApiVersionReader>();
    }
}

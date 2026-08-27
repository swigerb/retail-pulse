using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetailPulse.Api.Security;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Behavioural coverage for the REAL rate-limiting policies registered by
/// <see cref="RateLimitingSetup.AddRetailPulseRateLimiting"/>.
///
/// <para>
/// These tests drive traffic through a live <see cref="TestServer"/> that has the production
/// limiter registration applied, rather than restating the expected numbers in a local
/// fixture. The previous version of this file asserted a private dictionary against itself,
/// so raising the chat limit from 10/min to 999,999/min — removing DoS and cost protection
/// from the most expensive endpoints in the product — left every test green. A limit
/// regression must fail here.
/// </para>
/// </summary>
public sealed class RateLimitingConfigTests
{
    private static async Task<IHost> StartServerAsync(
        string policyName,
        Dictionary<string, string?>? configuration = null)
    {
        return await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .ConfigureAppConfiguration(cfg =>
                    cfg.AddInMemoryCollection(configuration ?? []))
                .ConfigureServices((context, services) =>
                {
                    services.AddRouting();
                    services.AddRetailPulseRateLimiting(context.Configuration);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints =>
                        endpoints.MapGet("/probe", () => Results.Ok("ok"))
                            .RequireRateLimiting(policyName));
                }))
            .StartAsync();
    }

    /// <summary>Sends <paramref name="count"/> sequential requests and returns the status codes in order.</summary>
    private static async Task<List<HttpStatusCode>> ProbeAsync(IHost host, int count)
    {
        HttpClient client = host.GetTestClient();
        var codes = new List<HttpStatusCode>(count);

        for (int i = 0; i < count; i++)
        {
            HttpResponseMessage response = await client.GetAsync("/probe");
            codes.Add(response.StatusCode);
        }

        return codes;
    }

    /// <summary>Sends sequential requests, rotating the forgeable X-Forwarded-For header.</summary>
    private static async Task<List<HttpStatusCode>> ProbeRotatingForwardedForAsync(IHost host, int count)
    {
        HttpClient client = host.GetTestClient();
        var codes = new List<HttpStatusCode>(count);

        for (int i = 0; i < count; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
            request.Headers.Add("X-Forwarded-For", $"203.0.113.{i}");
            HttpResponseMessage response = await client.SendAsync(request);
            codes.Add(response.StatusCode);
        }

        return codes;
    }

    [Theory]
    [Trait("OWASP", "A07-AuthFailures")]
    [InlineData("strict", RateLimitingSetup.StrictPermitLimit)]
    [InlineData("upload", RateLimitingSetup.UploadPermitLimit)]
    [InlineData("moderate", RateLimitingSetup.ModeratePermitLimit)]
    [InlineData("relaxed", RateLimitingSetup.RelaxedPermitLimit)]
    public async Task CorePolicy_AllowsExactlyItsPermitLimit_ThenRejects(string policyName, int permitLimit)
    {
        using IHost host = await StartServerAsync(policyName);

        List<HttpStatusCode> codes = await ProbeAsync(host, permitLimit + 1);

        codes.Take(permitLimit).Should().AllBeEquivalentTo(
            HttpStatusCode.OK,
            $"the '{policyName}' policy must admit exactly {permitLimit} requests per window");

        codes[permitLimit].Should().Be(
            HttpStatusCode.TooManyRequests,
            $"request {permitLimit + 1} must be rejected by the '{policyName}' policy");
    }

    [Fact]
    [Trait("OWASP", "A07-AuthFailures")]
    public async Task StrictPolicy_ThrottlesWellBeforeTwentyRequests_GuardingAiSpend()
    {
        // Independent of the exact constant: the AI-intensive tier must never admit a large
        // burst, because every admitted request is a paid model call.
        using IHost host = await StartServerAsync("strict");

        List<HttpStatusCode> codes = await ProbeAsync(host, 20);

        codes.Should().Contain(
            HttpStatusCode.TooManyRequests,
            "the strict tier must throttle well before 20 requests in a single window");
    }

    [Fact]
    [Trait("OWASP", "A07-AuthFailures")]
    public async Task RejectionStatusCode_Is429_NotASilentPass()
    {
        using IHost host = await StartServerAsync("upload");

        List<HttpStatusCode> codes = await ProbeAsync(host, RateLimitingSetup.UploadPermitLimit + 1);

        codes[RateLimitingSetup.UploadPermitLimit].Should().Be(
            HttpStatusCode.TooManyRequests,
            "an over-limit request must be rejected with 429, never silently admitted");
    }

    [Fact]
    [Trait("OWASP", "A07-AuthFailures")]
    public void CoreTiers_AreOrderedFromMostToLeastRestrictive()
    {
        RateLimitingSetup.UploadPermitLimit.Should().BeLessThan(RateLimitingSetup.StrictPermitLimit);
        RateLimitingSetup.StrictPermitLimit.Should().BeLessThan(RateLimitingSetup.ModeratePermitLimit);
        RateLimitingSetup.ModeratePermitLimit.Should().BeLessThan(RateLimitingSetup.RelaxedPermitLimit);

        RateLimitingSetup.CorePolicies.Should().HaveCount(4);
        RateLimitingSetup.CorePolicies.Values.Should().AllSatisfy(limit => limit.Should().BePositive());
    }

    [Fact]
    [Trait("OWASP", "A07-AuthFailures")]
    public void EveryPolicy_UsesAOneMinuteWindow()
    {
        RateLimitingSetup.Window.Should().Be(TimeSpan.FromMinutes(1));
    }

    // ── Anonymous bootstrap (mode-conditional, global per replica) ─────────────

    [Fact]
    [Trait("OWASP", "A07-AuthFailures")]
    public async Task AnonymousBootstrap_CapsSessionMintingAtConservativeDefault()
    {
        using IHost host = await StartServerAsync("anonymous-bootstrap");

        List<HttpStatusCode> codes = await ProbeAsync(
            host, RateLimitingSetup.AnonymousBootstrapDefaultPermitLimit + 1);

        codes[RateLimitingSetup.AnonymousBootstrapDefaultPermitLimit]
            .Should().Be(HttpStatusCode.TooManyRequests,
                "anonymous session minting must be capped at the conservative default");
    }

    [Fact]
    [Trait("OWASP", "A07-AuthFailures")]
    public void AnonymousBootstrap_IsAtLeastAsRestrictiveAsTheStrictestCoreTier()
    {
        RateLimitingSetup.AnonymousBootstrapDefaultPermitLimit
            .Should().BeLessThanOrEqualTo(RateLimitingSetup.StrictPermitLimit,
                "bootstrapping a credential must be at least as restricted as the strictest core policy");
    }

    [Fact]
    [Trait("OWASP", "A07-AuthFailures")]
    public async Task AnonymousBootstrap_HonoursLegacyPerIpKeyAsFallback()
    {
        using IHost host = await StartServerAsync(
            "anonymous-bootstrap",
            new Dictionary<string, string?> { ["Anonymous:Bootstrap:PerIpPerMinute"] = "2" });

        List<HttpStatusCode> codes = await ProbeAsync(host, 3);

        codes[2].Should().Be(HttpStatusCode.TooManyRequests,
            "the legacy PerIpPerMinute key must still be honoured as a backward-compatible fallback");
    }

    [Fact]
    [Trait("OWASP", "A07-AuthFailures")]
    public async Task AnonymousBootstrap_PrefersGlobalKeyOverLegacyKey()
    {
        using IHost host = await StartServerAsync(
            "anonymous-bootstrap",
            new Dictionary<string, string?>
            {
                ["Anonymous:Bootstrap:GlobalPerMinute"] = "1",
                ["Anonymous:Bootstrap:PerIpPerMinute"] = "50",
            });

        List<HttpStatusCode> codes = await ProbeAsync(host, 2);

        codes[0].Should().Be(HttpStatusCode.OK);
        codes[1].Should().Be(HttpStatusCode.TooManyRequests,
            "the current GlobalPerMinute key must win over the legacy fallback");
    }

    [Fact]
    [Trait("OWASP", "A07-AuthFailures")]
    public async Task AnonymousBootstrap_IsGlobal_NotPartitionedByForgeableClientHeaders()
    {
        // Behind Azure Container Apps, X-Forwarded-For is attacker-controlled. If the bootstrap
        // limiter were partitioned on it, an attacker could shard around the cap and farm
        // anonymous session tokens without limit.
        using IHost host = await StartServerAsync(
            "anonymous-bootstrap",
            new Dictionary<string, string?> { ["Anonymous:Bootstrap:GlobalPerMinute"] = "2" });

        List<HttpStatusCode> codes = await ProbeRotatingForwardedForAsync(host, 3);

        codes[2].Should().Be(HttpStatusCode.TooManyRequests,
            "rotating X-Forwarded-For must NOT grant additional bootstrap capacity");
    }

    // ── GitHub BFF login-flow limiters ────────────────────────────────────────

    [Theory]
    [Trait("OWASP", "A07-AuthFailures")]
    [InlineData("github-start", RateLimitingSetup.GitHubStartDefaultPermitLimit)]
    [InlineData("github-exchange", RateLimitingSetup.GitHubExchangeDefaultPermitLimit)]
    public async Task GitHubBffPolicy_CapsLoginFlowAbuse(string policyName, int permitLimit)
    {
        using IHost host = await StartServerAsync(policyName);

        List<HttpStatusCode> codes = await ProbeAsync(host, permitLimit + 1);

        codes[permitLimit].Should().Be(HttpStatusCode.TooManyRequests,
            $"the '{policyName}' policy must cap login-flow abuse at {permitLimit} per window");
    }

    [Fact]
    [Trait("OWASP", "A07-AuthFailures")]
    public async Task GitHubStart_IsGlobal_NotPartitionedByForgeableClientHeaders()
    {
        using IHost host = await StartServerAsync(
            "github-start",
            new Dictionary<string, string?> { ["GitHub:RateLimits:StartPerMinute"] = "2" });

        List<HttpStatusCode> codes = await ProbeRotatingForwardedForAsync(host, 3);

        codes[2].Should().Be(HttpStatusCode.TooManyRequests,
            "rotating X-Forwarded-For must NOT grant additional login-start capacity");
    }
}

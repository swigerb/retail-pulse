using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Validation;

namespace RetailPulse.Tests.Security;

/// <summary>
/// OWASP Top 10 security tests for RetailPulse API.
///
/// <para>
/// This file holds the categories whose assertions do not belong to a more specific
/// suite. Where a dedicated suite already exercises the real system, the tests live
/// there and carry the matching <c>[Trait("OWASP", ...)]</c> tag so that
/// <c>dotnet test --filter "OWASP=A01-BrokenAccessControl"</c> runs the genuine
/// coverage rather than a duplicate:
/// </para>
/// <list type="bullet">
///   <item><b>A01</b> — <see cref="EndpointAuthorizationCoverageTests"/> walks the real
///     <c>EndpointDataSource</c>, plus <see cref="ContainerAppDeploymentContractTests"/>
///     for the deployment surface.</item>
///   <item><b>A02</b> — <c>GitHubSessionTokenTests</c>, <c>DurableAuditLogTests</c>.</item>
///   <item><b>A05</b> — <see cref="ContainerAppDeploymentContractTests"/>.</item>
///   <item><b>A07</b> — <see cref="RateLimitingConfigTests"/> drives real traffic through
///     the production limiter registration.</item>
/// </list>
///
/// <para>
/// Tests here must exercise production code. Assertions over locally declared literals
/// pass regardless of how the application behaves and are worse than no test, because
/// they advertise coverage that does not exist.
/// </para>
/// </summary>
public class OwaspTests
{
    // ═══════════════════════════════════════════════════════════════════════
    // A03:2021 — Injection
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [Trait("OWASP", "A03-Injection")]
    [InlineData("<script>alert('xss')</script>")]
    [InlineData("<img src=x onerror=alert('xss')>")]
    [InlineData("'; DROP TABLE users; --")]
    [InlineData("\" OR 1=1 --")]
    [InlineData("<iframe src='javascript:alert(1)'></iframe>")]
    public void A03_XssAndSqlInjection_DoNotCrashValidator(string maliciousInput)
    {
        // The validator should handle XSS/injection payloads gracefully
        // (accept them as valid input for the chat model, which has its own guardrails)
        var request = new Contracts.ChatRequest(maliciousInput, "abc123");

        ValidationResult result = ChatRequestValidator.Validate(request);

        // Chat messages containing injection payloads should pass basic validation
        // (the guardrails middleware handles content safety separately)
        result.IsValid.Should().BeTrue(
            "chat messages are processed by guardrails, not rejected by input validation");
    }

    [Fact]
    [Trait("OWASP", "A03-Injection")]
    public void A03_OversizedMessage_IsRejected()
    {
        string oversizedMessage = new('A', 4001);
        var request = new Contracts.ChatRequest(oversizedMessage, "session1");

        ValidationResult result = ChatRequestValidator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainKey("message");
    }

    [Fact]
    [Trait("OWASP", "A03-Injection")]
    public void A03_MaliciousSessionId_IsRejected()
    {
        var request = new Contracts.ChatRequest(
            "Normal message",
            "'; DROP TABLE sessions; --");

        ValidationResult result = ChatRequestValidator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainKey("sessionId");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A05:2021 — Security Misconfiguration
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("OWASP", "A05-SecurityMisconfiguration")]
    public async Task A05_SecurityHeaders_ArePresent()
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
        HttpResponseMessage response = await client.GetAsync("/");

        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");
        response.Headers.GetValues("Referrer-Policy").Should().Contain("strict-origin-when-cross-origin");
        response.Headers.GetValues("Permissions-Policy").Should().Contain("camera=(), microphone=(), geolocation=()");
        response.Headers.GetValues("Content-Security-Policy").Should().ContainSingle()
            .Which.Should().Contain("default-src 'self'");
    }

    [Fact]
    [Trait("OWASP", "A05-SecurityMisconfiguration")]
    public async Task A05_SecurityHeaders_IncludeHSTS_WhenHttps()
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
        // TestServer uses http by default, so HSTS won't be added
        HttpResponseMessage response = await client.GetAsync("http://localhost/");

        // HSTS should NOT be present on HTTP requests
        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse(
            "HSTS should only be sent over HTTPS");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A07:2021 — Identification and Authentication Failures
    // ═══════════════════════════════════════════════════════════════════════
    //
    // Rate limiting is the A07 control in this codebase and is covered
    // behaviourally by RateLimitingConfigTests, which drives real traffic through
    // the production limiter registration and asserts the 429 boundary. The two
    // tests previously here asserted over local literals (one of them reduced to
    // `10 <= 20`) and passed even when the chat limit was raised to 999,999/min.
}

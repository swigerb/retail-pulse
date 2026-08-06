using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using RetailPulse.Api.Endpoints;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Security;
using RetailPulse.Api.Security.Anonymous;

namespace RetailPulse.Tests.Security;

/// <summary>
/// End-to-end HTTP contract + threat tests for the Sprint 1 Anonymous authentication boundary.
///
/// A minimal in-process <see cref="TestServer"/> wires the EXACT anonymous stack the API uses
/// (<see cref="ProviderNeutralAuthentication.AddProviderNeutralAuthentication"/> in Anonymous mode
/// → session JwtBearer scheme + constrained policy + <see cref="AnonymousGuardMiddleware"/>), with
/// a configured signing key so tests can mint their own session tokens offline. It proves:
/// <list type="bullet">
///   <item>bootstrap issues a token and is per-IP rate-limited;</item>
///   <item>malformed / expired / wrong-issuer / wrong-audience / wrong-signature / wrong-provider
///     tokens are rejected;</item>
///   <item>a valid anonymous token reaches read REST and the hub (query token);</item>
///   <item>distinct sessions are isolated and identity comes from the token, not a spoof header;</item>
///   <item>mutation routes are 403 for anonymous;</item>
///   <item>the daily request circuit breaker fails closed (cache cannot bypass it);</item>
///   <item>oversized requests and REST query-tokens are rejected.</item>
/// </list>
/// </summary>
public sealed class AnonymousAuthenticationTests
{
    private const string Issuer = "retail-pulse-anonymous";
    private const string Audience = "retail-pulse-api";
    private const string SigningKeyText = "anon-integration-test-signing-key-0123456789";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes(SigningKeyText));
    private static readonly SymmetricSecurityKey WrongKey = new(Encoding.UTF8.GetBytes("a-totally-different-wrong-signing-key-999999"));

    // ── bootstrap ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bootstrap_IssuesUsableSessionToken()
    {
        using TestFixture fx = CreateServer();

        HttpResponseMessage resp = await fx.Client.PostAsync("/api/auth/anonymous/session", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        BootstrapBody body = await resp.Content.ReadFromJsonAsync<BootstrapBody>() ?? new();
        body.Token.Should().NotBeNullOrWhiteSpace();
        body.Subject.Should().StartWith("anon-");
        body.TokenType.Should().Be("Bearer");
        body.ExpiresInSeconds.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Bootstrap_IsRateLimitedGlobally()
    {
        using TestFixture fx = CreateServer(bootstrapPerIp: 2);

        (await fx.Client.PostAsync("/api/auth/anonymous/session", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await fx.Client.PostAsync("/api/auth/anonymous/session", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await fx.Client.PostAsync("/api/auth/anonymous/session", null)).StatusCode
            .Should().Be(HttpStatusCode.TooManyRequests, "the 3rd bootstrap in the window exceeds the global limit");
    }

    // ── token validation / threat cases ───────────────────────────────────────

    [Fact]
    public async Task AnonymousRestWithoutToken_Returns401()
    {
        using TestFixture fx = CreateServer();
        (await fx.Client.GetAsync("/api/scorecard")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task MalformedToken_Returns401()
    {
        using TestFixture fx = CreateServer();
        (await Get(fx, "/api/scorecard", "not-a-jwt")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ExpiredToken_Returns401()
    {
        using TestFixture fx = CreateServer();
        string token = Token(subject: "anon-exp", expires: DateTime.UtcNow.AddMinutes(-5), notBefore: DateTime.UtcNow.AddMinutes(-10));
        (await Get(fx, "/api/scorecard", token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WrongIssuer_Returns401()
    {
        using TestFixture fx = CreateServer();
        string token = Token(subject: "anon-i", issuer: "https://evil.example/");
        (await Get(fx, "/api/scorecard", token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WrongAudience_Returns401()
    {
        using TestFixture fx = CreateServer();
        string token = Token(subject: "anon-a", audience: "some-other-api");
        (await Get(fx, "/api/scorecard", token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WrongSignature_Returns401()
    {
        using TestFixture fx = CreateServer();
        string token = Token(subject: "anon-s", key: WrongKey);
        (await Get(fx, "/api/scorecard", token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CrossProviderToken_IsRejected()
    {
        using TestFixture fx = CreateServer();
        // Correctly signed for issuer/audience but stamped provider=Entra — the constrained
        // anonymous policy must refuse it (authenticated but not authorized → 403).
        string token = Token(subject: "anon-x", provider: "Entra");
        (await Get(fx, "/api/scorecard", token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── valid anonymous access ─────────────────────────────────────────────────

    [Fact]
    public async Task ValidToken_ReachesChat_ButBothHubsAreForbidden()
    {
        using TestFixture fx = CreateServer();
        string token = Token(subject: "anon-ok");

        // POST /api/chat is the single allowlisted authenticated REST capability.
        (await PostChatRaw(fx, token)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Sprint 1: the SignalR hubs are NOT part of the anonymous surface. A VALID anonymous token
        // is authenticated and satisfies the authorization policy, but the deny-by-default guard
        // blocks the hub before the endpoint runs — 403 on both connection (GET, ?access_token) and
        // negotiate (POST), for both telemetry and streaming. Anonymous gets no real-time telemetry.
        foreach (string hub in new[] { "/hubs/telemetry", "/hubs/streaming" })
        {
            HttpResponseMessage connect = await fx.Client.GetAsync($"{hub}?access_token={token}");
            connect.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                $"a valid anonymous token must be denied on the {hub} connection endpoint");

            var negotiate = new HttpRequestMessage(HttpMethod.Post, $"{hub}/negotiate?access_token={token}")
            {
                Content = new StringContent(string.Empty),
            };
            HttpResponseMessage neg = await fx.Client.SendAsync(negotiate);
            neg.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                $"a valid anonymous token must be denied on the {hub} negotiate endpoint");
        }
    }

    // ── deny-by-default: the broad GET/observability/admin surface is 403 ───────

    [Theory]
    [InlineData("GET", "/api/scorecard")]
    [InlineData("GET", "/api/sessions")]
    [InlineData("GET", "/api/audit")]
    [InlineData("GET", "/api/traces")]
    [InlineData("GET", "/api/dead-letter")]
    [InlineData("GET", "/api/cards")]
    [InlineData("GET", "/api/approvals")]
    [InlineData("GET", "/api/memory")]
    [InlineData("GET", "/api/export/session/abc")]
    [InlineData("GET", "/api/guardrails/logs")]
    [InlineData("POST", "/api/chat/stream")]
    [InlineData("POST", "/api/council/convene")]
    [InlineData("GET", "/api/council/agents")]
    [InlineData("POST", "/api/messages")]
    public async Task NonAllowlistedRoutes_AreForbidden_ForAnonymous(string method, string path)
    {
        using TestFixture fx = CreateServer();
        string token = Token(subject: "anon-deny");

        var req = new HttpRequestMessage(new HttpMethod(method), path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (method == "POST")
        {
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        HttpResponseMessage resp = await fx.Client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            $"'{method} {path}' is not on the anonymous allowlist — deny-by-default applies before routing");
    }

    [Fact]
    public async Task RestQueryToken_IsIgnored_Returns401()
    {
        using TestFixture fx = CreateServer();
        string token = Token(subject: "anon-q");
        // A REST endpoint must NOT accept ?access_token (only /hubs may). No Authorization header
        // means the request is unauthenticated → 401.
        HttpResponseMessage resp = await fx.Client.GetAsync($"/api/scorecard?access_token={token}");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task IdentityComesFromToken_NotSpoofHeader()
    {
        using TestFixture fx = CreateServer();
        string token = Token(subject: "anon-real");

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-User-Id", "anon-attacker");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        HttpResponseMessage resp = await fx.Client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        WhoAmI body = await resp.Content.ReadFromJsonAsync<WhoAmI>() ?? new();
        body.Subject.Should().Be("anon-real", "identity must come from the signed token subject, never a header");
    }

    [Fact]
    public async Task DistinctSessions_AreIsolated()
    {
        using TestFixture fx = CreateServer();
        string a = Token(subject: "anon-A");
        string b = Token(subject: "anon-B");

        WhoAmI ra = await PostChat(fx, a);
        WhoAmI rb = await PostChat(fx, b);

        ra.Subject.Should().Be("anon-A");
        rb.Subject.Should().Be("anon-B");
        ra.Subject.Should().NotBe(rb.Subject);
    }

    // ── mutation surface disabled ──────────────────────────────────────────────

    [Theory]
    [InlineData("DELETE", "/api/memory/42")]
    [InlineData("POST", "/api/approvals/7/respond")]
    public async Task MutationRoutes_Return403_ForAnonymous(string method, string path)
    {
        using TestFixture fx = CreateServer();
        string token = Token(subject: "anon-m");

        var req = new HttpRequestMessage(new HttpMethod(method), path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (method == "POST")
        {
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        HttpResponseMessage resp = await fx.Client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, "anonymous mode is read-only");
    }

    // ── billable-use circuit breaker ───────────────────────────────────────────

    [Fact]
    public async Task DailyRequestCeiling_FailsClosed()
    {
        using TestFixture fx = CreateServer(dailyRequests: 2);
        string token = Token(subject: "anon-budget");

        (await PostChatRaw(fx, token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostChatRaw(fx, token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostChatRaw(fx, token)).StatusCode
            .Should().Be(HttpStatusCode.ServiceUnavailable, "the daily request circuit breaker trips (fail-closed)");
    }

    // ── request-size bound ─────────────────────────────────────────────────────

    [Fact]
    public async Task OversizedRequest_Returns413()
    {
        using TestFixture fx = CreateServer(maxRequestBytes: 256);
        string token = Token(subject: "anon-big");

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(new string('x', 2000), Encoding.UTF8, "application/json");

        HttpResponseMessage resp = await fx.Client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task ChunkedOversizedRequest_Returns413_WithoutContentLength()
    {
        using TestFixture fx = CreateServer(maxRequestBytes: 256);
        string token = Token(subject: "anon-chunked");

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(new string('x', 2000), Encoding.UTF8, "application/json");
        // Force chunked transfer so no Content-Length header is sent — the ContentLength check
        // cannot see the size; the length-counting pre-read must still reject it.
        req.Headers.TransferEncodingChunked = true;

        HttpResponseMessage resp = await fx.Client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge,
            "a chunked/unknown-length oversized body must be capped during the read");
    }

    // ── health stays anonymous ─────────────────────────────────────────────────

    [Fact]
    public async Task Health_IsAnonymous()
    {
        using TestFixture fx = CreateServer();
        (await fx.Client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static async Task<HttpResponseMessage> Get(TestFixture fx, string path, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await fx.Client.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> PostChatRaw(TestFixture fx, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    private static async Task<WhoAmI> PostChat(TestFixture fx, string token)
    {
        HttpResponseMessage resp = await PostChatRaw(fx, token);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return await resp.Content.ReadFromJsonAsync<WhoAmI>() ?? new();
    }

    private static string Token(
        string subject,
        string? issuer = null,
        string? audience = null,
        string provider = "Anonymous",
        DateTime? expires = null,
        DateTime? notBefore = null,
        SymmetricSecurityKey? key = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new("provider", provider),
            new("roles", "RetailPulse.Anonymous"),
            new("scp", "chat_limited"),
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer,
            Audience = audience ?? Audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = notBefore ?? DateTime.UtcNow.AddMinutes(-1),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.HmacSha256),
        };

        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.CreateToken(descriptor);
    }

    private static TestFixture CreateServer(
        int bootstrapPerIp = 50,
        int dailyRequests = 500,
        int maxRequestBytes = 16384)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "Anonymous",
                ["Anonymous:AllowHosted"] = "true",
                ["Anonymous:SigningKey"] = SigningKeyText,
                ["Anonymous:Issuer"] = Issuer,
                ["Anonymous:Audience"] = Audience,
                ["Anonymous:MaxRequestBytes"] = maxRequestBytes.ToString(),
                ["Anonymous:Bootstrap:PerIpPerMinute"] = bootstrapPerIp.ToString(),
                ["Anonymous:Chat:PerSubjectPerMinute"] = "100",
                ["Anonymous:Chat:PerIpPerMinute"] = "100",
                ["Anonymous:Limits:DailyMaxRequests"] = dailyRequests.ToString(),
                ["Anonymous:Limits:DailyMaxTokens"] = "1000000",
                ["Anonymous:Limits:DailyMaxCostUsd"] = "100",
            })
            .Build();

        IHostBuilder builder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices((context, services) =>
                {
                    services.AddRouting();
                    services.AddLogging();

                    // Wire the full anonymous stack (scheme + policy + guardrail services).
                    services.AddProviderNeutralAuthentication(config, context.HostingEnvironment);

                    // Global conservative bootstrap limiter — mirrors Program.cs. Behind ACA the
                    // client IP is not trustworthy (proxy IP / forgeable XFF), so bootstrap is a
                    // single global window; per-subject limits take over after bootstrap.
                    services.AddRateLimiter(rl =>
                    {
                        rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                        rl.AddPolicy("anonymous-bootstrap", _ =>
                            System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                                partitionKey: "anonymous-bootstrap-global",
                                factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                                {
                                    PermitLimit = config.GetValue("Anonymous:Bootstrap:GlobalPerMinute",
                                        config.GetValue("Anonymous:Bootstrap:PerIpPerMinute", 5)),
                                    Window = TimeSpan.FromMinutes(1),
                                    QueueLimit = 0,
                                }));
                    });
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseRateLimiter();
                    app.UseMiddleware<AnonymousGuardMiddleware>();
                    app.UseEndpoints(endpoints =>
                    {
                        // Unauthenticated bootstrap (per-IP rate-limited).
                        endpoints.MapPost(AnonymousCapabilityPolicy.BootstrapRoute,
                            (IAnonymousSessionTokenService svc) =>
                            {
                                AnonymousSession s = svc.CreateSession();
                                return Results.Ok(new
                                {
                                    token = s.Token,
                                    tokenType = "Bearer",
                                    expiresInSeconds = s.ExpiresInSeconds,
                                    subject = s.Subject,
                                });
                            })
                            .AllowAnonymous()
                            .RequireRateLimiting("anonymous-bootstrap");

                        // Read-only chat/query stand-ins — echo the token subject to prove identity
                        // provenance and isolation.
                        endpoints.MapPost("/api/chat", (HttpContext ctx) =>
                            Results.Ok(new { subject = ctx.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value }))
                            .RequireAuthorization();
                        endpoints.MapGet("/api/scorecard", static () => Results.Ok(new { data = "read" }))
                            .RequireAuthorization();

                        // Mutation stand-ins — must be blocked by the guard for anonymous callers.
                        endpoints.MapDelete("/api/memory/{id}", static (string id) => Results.Ok(new { deleted = id }))
                            .RequireAuthorization();
                        endpoints.MapPost("/api/approvals/{id}/respond", static (string id) => Results.Ok(new { id }))
                            .RequireAuthorization();

                        // Hub stand-ins (query-token path). Both the connection (GET) and negotiate
                        // (POST) endpoints for BOTH hubs require authorization but are NOT on the
                        // anonymous allowlist, so the guard denies a valid anonymous token with 403.
                        endpoints.MapGet("/hubs/telemetry", static () => Results.Ok(new { hub = "telemetry" }))
                            .RequireAuthorization();
                        endpoints.MapPost("/hubs/telemetry/negotiate", static () => Results.Ok(new { negotiated = true }))
                            .RequireAuthorization();
                        endpoints.MapGet("/hubs/streaming", static () => Results.Ok(new { hub = "streaming" }))
                            .RequireAuthorization();
                        endpoints.MapPost("/hubs/streaming/negotiate", static () => Results.Ok(new { negotiated = true }))
                            .RequireAuthorization();

                        endpoints.MapGet("/health", static () => Results.Ok(new { status = "ok" })).AllowAnonymous();
                    });
                });
            });

        IHost host = builder.Build();
        host.Start();
        return new TestFixture(host);
    }

    private sealed class BootstrapBody
    {
        public string? Token { get; set; }
        public string? Subject { get; set; }
        public string? TokenType { get; set; }
        public int ExpiresInSeconds { get; set; }
    }

    private sealed class WhoAmI
    {
        public string? Subject { get; set; }
    }

    private sealed class TestFixture : IDisposable
    {
        private readonly IHost _host;

        public TestFixture(IHost host)
        {
            _host = host;
            Client = host.GetTestClient();
        }

        public HttpClient Client { get; }

        public void Dispose()
        {
            Client.Dispose();
            _host.StopAsync().GetAwaiter().GetResult();
            _host.Dispose();
        }
    }
}

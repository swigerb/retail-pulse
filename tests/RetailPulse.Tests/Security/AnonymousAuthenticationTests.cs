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
    public async Task Bootstrap_IsRateLimitedPerIp()
    {
        using TestFixture fx = CreateServer(bootstrapPerIp: 2);

        (await fx.Client.PostAsync("/api/auth/anonymous/session", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await fx.Client.PostAsync("/api/auth/anonymous/session", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await fx.Client.PostAsync("/api/auth/anonymous/session", null)).StatusCode
            .Should().Be(HttpStatusCode.TooManyRequests, "the 3rd bootstrap in the window exceeds the per-IP limit");
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
    public async Task ValidToken_ReachesReadRestAndHub()
    {
        using TestFixture fx = CreateServer();
        string token = Token(subject: "anon-ok");

        (await Get(fx, "/api/scorecard", token)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Hub: the token is supplied via ?access_token (WebSocket handshakes cannot set headers).
        HttpResponseMessage hub = await fx.Client.GetAsync($"/hubs/telemetry?access_token={token}");
        hub.StatusCode.Should().Be(HttpStatusCode.OK);
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

                    // Rate limiter with the anonymous-bootstrap per-IP policy (mirrors Program.cs).
                    services.AddRateLimiter(rl =>
                    {
                        rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                        rl.AddPolicy("anonymous-bootstrap", httpContext =>
                            System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                                factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                                {
                                    PermitLimit = config.GetValue("Anonymous:Bootstrap:PerIpPerMinute", 5),
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

                        // Hub stand-in (query-token path).
                        endpoints.MapGet("/hubs/telemetry", static () => Results.Ok(new { hub = "telemetry" }))
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

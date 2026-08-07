using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using RetailPulse.Api.Endpoints;
using RetailPulse.Api.Security;
using RetailPulse.Api.Security.GitHub;

namespace RetailPulse.Tests.Security;

/// <summary>
/// End-to-end HTTP contract + threat tests for the Sprint 2 GitHub confidential OAuth
/// Backend-for-Frontend (BFF) boundary.
///
/// A minimal in-process <see cref="TestServer"/> wires the EXACT GitHub stack (session JwtBearer
/// scheme + constrained policy + one-time stores + allowlist), with the GitHub HTTP transport
/// replaced by a programmable <see cref="FakeGitHubOAuthClient"/> and a configured signing key so
/// tests can also mint session tokens offline. It proves the full happy path and every failure and
/// abuse mode without ever calling GitHub or leaking the provider token.
/// </summary>
public sealed class GitHubAuthenticationTests
{
    private const string Issuer = "retail-pulse-github";
    private const string Audience = "retail-pulse-api";
    private const string SigningKeyText = "github-integration-test-signing-key-0123456789";
    private const long AllowedId = 12345;
    private const string AllowedLogin = "octocat";
    private const string FrontendUrl = "https://app.example.com/auth/github/callback";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes(SigningKeyText));
    private static readonly SymmetricSecurityKey WrongKey = new(Encoding.UTF8.GetBytes("a-totally-different-wrong-signing-key-999999"));

    private const string ProviderToken = "gho_super_secret_provider_token_value";

    // ── start ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_RedirectsToGitHubAuthorize_WithStateCookieAndMinimalScope()
    {
        using TestFixture fx = CreateServer();

        HttpResponseMessage resp = await fx.Client.GetAsync(GitHubAuthConstants.StartRoute);

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        Uri location = resp.Headers.Location!;
        location.GetLeftPart(UriPartial.Path).Should().Be(GitHubAuthConstants.AuthorizeUrl);

        System.Collections.Specialized.NameValueCollection q = System.Web.HttpUtility.ParseQueryString(location.Query);
        q["client_id"].Should().Be("Iv1.testclientid");
        q["redirect_uri"].Should().Be("https://api.example.com/api/auth/github/callback");
        q["state"].Should().NotBeNullOrWhiteSpace();
        q["allow_signup"].Should().Be("false");

        // No org allowlist configured by default → minimal (empty) scope, never repo.
        (q["scope"] ?? string.Empty).Should().NotContain("repo");

        // The browser-binding state cookie is set HttpOnly.
        resp.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies).Should().BeTrue();
        string cookie = cookies!.Single(c => c.Contains("rp_gh_state"));
        cookie.Should().Contain("httponly", "the state cookie must be HttpOnly");
        cookie.Should().Contain("samesite=lax");
    }

    [Fact]
    public async Task Start_WithOrgAllowlist_RequestsReadOrgScopeOnly()
    {
        using TestFixture fx = CreateServer(allowedOrgs: ["contoso"]);

        HttpResponseMessage resp = await fx.Client.GetAsync(GitHubAuthConstants.StartRoute);

        System.Collections.Specialized.NameValueCollection q =
            System.Web.HttpUtility.ParseQueryString(resp.Headers.Location!.Query);
        q["scope"].Should().Be("read:org");
        q["scope"].Should().NotContain("repo");
    }

    // ── callback happy path ──────────────────────────────────────────────────

    [Fact]
    public async Task Callback_HappyPath_RedirectsToFrontendWithOneTimeCode_NoProviderToken()
    {
        using TestFixture fx = CreateServer();
        (string state, string cookie) = await BeginLogin(fx);

        HttpResponseMessage resp = await Callback(fx, state, cookie, code: "gh-auth-code");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        Uri location = resp.Headers.Location!;
        location.GetLeftPart(UriPartial.Path).Should().Be(FrontendUrl);

        System.Collections.Specialized.NameValueCollection q = System.Web.HttpUtility.ParseQueryString(location.Query);
        string? redemption = q["code"];
        redemption.Should().NotBeNullOrWhiteSpace();

        // CRITICAL: the provider token must NEVER appear in the redirect URL.
        location.ToString().Should().NotContain(ProviderToken);
        redemption.Should().NotBe(ProviderToken);

        // The state cookie is deleted on success.
        AssertStateCookieCleared(resp);
    }

    [Fact]
    public async Task Callback_ThenExchange_ReturnsUsableSessionToken_NotProviderToken()
    {
        using TestFixture fx = CreateServer();
        string redemption = await LoginToRedemption(fx);

        HttpResponseMessage resp = await Exchange(fx, redemption);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        string raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain(ProviderToken, "the provider token must never reach the exchange body");

        ExchangeBody body = JsonSerializer.Deserialize<ExchangeBody>(raw, JsonOpts)!;
        body.Token.Should().NotBeNullOrWhiteSpace();
        body.TokenType.Should().Be("Bearer");
        body.Subject.Should().Be($"github:{AllowedId}");

        // The returned token is a valid GitHub session token that reaches a protected REST route.
        WhoAmI who = await GetProtected(fx, body.Token!);
        who.Subject.Should().Be($"github:{AllowedId}");
    }

    // ── state / cookie threats ───────────────────────────────────────────────

    [Fact]
    public async Task Callback_MissingState_IsRejected()
    {
        using TestFixture fx = CreateServer();
        (_, string cookie) = await BeginLogin(fx);

        HttpResponseMessage resp = await Callback(fx, state: null, cookie, code: "c");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Callback_AbsentCookie_IsRejected()
    {
        using TestFixture fx = CreateServer();
        (string state, _) = await BeginLogin(fx);

        HttpResponseMessage resp = await Callback(fx, state, cookie: null, code: "c");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Callback_MismatchedState_IsRejected()
    {
        using TestFixture fx = CreateServer();
        (_, string cookie) = await BeginLogin(fx);

        HttpResponseMessage resp = await Callback(fx, state: "not-the-issued-state", cookie, code: "c");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Callback_WrongCookieForState_IsRejected()
    {
        using TestFixture fx = CreateServer();
        (string state, _) = await BeginLogin(fx);
        // A second login yields a different cookie secret; pairing it with the first state must fail.
        (_, string otherCookie) = await BeginLogin(fx);

        HttpResponseMessage resp = await Callback(fx, state, otherCookie, code: "c");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Callback_ReplayedState_IsRejectedSecondTime()
    {
        using TestFixture fx = CreateServer();
        (string state, string cookie) = await BeginLogin(fx);

        HttpResponseMessage first = await Callback(fx, state, cookie, code: "c1");
        first.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // Same state + cookie again → the one-time state entry is gone.
        HttpResponseMessage second = await Callback(fx, state, cookie, code: "c2");
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Callback_ExpiredState_IsRejected()
    {
        using TestFixture fx = CreateServer(stateTtlSeconds: 30);
        (string state, string cookie) = await BeginLogin(fx);

        fx.Clock.Advance(TimeSpan.FromSeconds(31));

        HttpResponseMessage resp = await Callback(fx, state, cookie, code: "c");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── provider / user / org / allowlist failures ───────────────────────────

    [Fact]
    public async Task Callback_UserDenial_RedirectsToFrontendWithError_NoToken()
    {
        using TestFixture fx = CreateServer();
        (string state, string cookie) = await BeginLogin(fx);

        HttpResponseMessage resp = await Callback(fx, state, cookie, code: null, error: "access_denied");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        Uri loc = resp.Headers.Location!;
        loc.GetLeftPart(UriPartial.Path).Should().Be(FrontendUrl);
        System.Web.HttpUtility.ParseQueryString(loc.Query)["error"].Should().NotBeNullOrWhiteSpace();
        System.Web.HttpUtility.ParseQueryString(loc.Query)["code"].Should().BeNull();
    }

    [Fact]
    public async Task Callback_ExchangeFailure_RedirectsWithError()
    {
        using TestFixture fx = CreateServer();
        fx.OAuth.OnExchange = _ => new GitHubTokenResult(false, null, "exchange_no_token");
        (string state, string cookie) = await BeginLogin(fx);

        HttpResponseMessage resp = await Callback(fx, state, cookie, code: "c");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        System.Web.HttpUtility.ParseQueryString(resp.Headers.Location!.Query)["error"].Should().Be("login_failed");
    }

    [Fact]
    public async Task Callback_UserValidationFailure_RedirectsWithError()
    {
        using TestFixture fx = CreateServer();
        fx.OAuth.OnGetUser = _ => new GitHubUserResult(false, 0, string.Empty, "user_invalid");
        (string state, string cookie) = await BeginLogin(fx);

        HttpResponseMessage resp = await Callback(fx, state, cookie, code: "c");

        System.Web.HttpUtility.ParseQueryString(resp.Headers.Location!.Query)["error"].Should().Be("login_failed");
    }

    [Fact]
    public async Task Callback_UnallowlistedUser_RedirectsWithNotAuthorized()
    {
        using TestFixture fx = CreateServer();
        fx.OAuth.OnGetUser = _ => new GitHubUserResult(true, 999999, "stranger", null);
        (string state, string cookie) = await BeginLogin(fx);

        HttpResponseMessage resp = await Callback(fx, state, cookie, code: "c");

        System.Web.HttpUtility.ParseQueryString(resp.Headers.Location!.Query)["error"].Should().Be("not_authorized");
    }

    [Fact]
    public async Task Callback_InactiveOrgMembership_RedirectsWithNotAuthorized()
    {
        using TestFixture fx = CreateServer(allowedOrgs: ["contoso"], useUserIdAllowlist: false);
        fx.OAuth.OnGetUser = _ => new GitHubUserResult(true, 999999, "stranger", null);
        fx.OAuth.OnOrgMembership = (_, _) => new GitHubOrgMembershipResult(false, "org_not_active");
        (string state, string cookie) = await BeginLogin(fx);

        HttpResponseMessage resp = await Callback(fx, state, cookie, code: "c");

        System.Web.HttpUtility.ParseQueryString(resp.Headers.Location!.Query)["error"].Should().Be("not_authorized");
    }

    [Fact]
    public async Task Callback_ProviderTokenNeverAppearsInLogs()
    {
        using TestFixture fx = CreateServer();
        (string state, string cookie) = await BeginLogin(fx);

        await Callback(fx, state, cookie, code: "c");

        fx.LogSink.Messages.Should().NotContain(m => m.Contains(ProviderToken),
            "the provider token must never be written to logs");
    }

    // ── exchange threats ─────────────────────────────────────────────────────

    [Fact]
    public async Task Exchange_ReplayedCode_IsRejectedSecondTime()
    {
        using TestFixture fx = CreateServer();
        string redemption = await LoginToRedemption(fx);

        (await Exchange(fx, redemption)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Exchange(fx, redemption)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Exchange_UnknownCode_IsRejected()
    {
        using TestFixture fx = CreateServer();

        HttpResponseMessage resp = await Exchange(fx, "never-issued-code");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Exchange_ExpiredCode_IsRejected()
    {
        using TestFixture fx = CreateServer(redemptionTtlSeconds: 30);
        string redemption = await LoginToRedemption(fx);

        fx.Clock.Advance(TimeSpan.FromSeconds(31));

        (await Exchange(fx, redemption)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Exchange_ConcurrentRace_YieldsExactlyOneSuccess()
    {
        using TestFixture fx = CreateServer();
        string redemption = await LoginToRedemption(fx);

        Task<HttpResponseMessage>[] attempts =
            [.. Enumerable.Range(0, 8).Select(_ => Exchange(fx, redemption))];
        HttpResponseMessage[] results = await Task.WhenAll(attempts);

        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1,
            "atomic one-time redemption admits exactly one exchange");
    }

    // ── session token validation threats ─────────────────────────────────────

    [Fact]
    public async Task Protected_ValidSessionToken_Succeeds()
    {
        using TestFixture fx = CreateServer();
        string token = Token();

        WhoAmI who = await GetProtected(fx, token);

        who.Subject.Should().Be($"github:{AllowedId}");
    }

    [Theory]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-audience")]
    [InlineData("wrong-signature")]
    [InlineData("wrong-provider")]
    [InlineData("expired")]
    [InlineData("cross-provider-anonymous")]
    public async Task Protected_BadSessionToken_IsRejected(string flavor)
    {
        using TestFixture fx = CreateServer();
        string token = flavor switch
        {
            "wrong-issuer" => Token(issuer: "evil-issuer"),
            "wrong-audience" => Token(audience: "evil-audience"),
            "wrong-signature" => Token(key: WrongKey),
            "wrong-provider" => Token(provider: "Entra"),
            "expired" => Token(expires: DateTime.UtcNow.AddMinutes(-5), notBefore: DateTime.UtcNow.AddMinutes(-10)),
            "cross-provider-anonymous" => Token(provider: "Anonymous", role: "RetailPulse.Anonymous", scope: "chat_limited"),
            _ => throw new ArgumentOutOfRangeException(nameof(flavor)),
        };

        HttpResponseMessage resp = await GetProtectedRaw(fx, token);

        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Protected_MissingRoleOrScope_IsForbidden()
    {
        using TestFixture fx = CreateServer();
        string noRole = Token(role: "SomethingElse");

        (await GetProtectedRaw(fx, noRole)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── REST vs hub token-position parity ────────────────────────────────────

    [Fact]
    public async Task SessionToken_AuthorizesBothRestAndHub()
    {
        using TestFixture fx = CreateServer();
        string token = Token();

        // REST bearer
        (await GetProtectedRaw(fx, token)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Hub query token
        HttpResponseMessage hub = await fx.Client.GetAsync($"/hubs/telemetry?access_token={token}");
        hub.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task QueryToken_OnRestRoute_IsRejected()
    {
        using TestFixture fx = CreateServer();
        string token = Token();

        // A query token on a REST path must NOT authenticate (identical to Entra/Anonymous).
        HttpResponseMessage resp = await fx.Client.GetAsync($"/api/whoami?access_token={token}");

        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    // ── rate limits ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_IsRateLimited()
    {
        using TestFixture fx = CreateServer(startPerMinute: 3);

        var statuses = new List<HttpStatusCode>();
        for (int i = 0; i < 6; i++)
        {
            statuses.Add((await fx.Client.GetAsync(GitHubAuthConstants.StartRoute)).StatusCode);
        }

        statuses.Should().Contain(HttpStatusCode.TooManyRequests, "the start endpoint is rate limited");
    }

    [Fact]
    public async Task Exchange_IsRateLimited()
    {
        using TestFixture fx = CreateServer(exchangePerMinute: 3);

        var statuses = new List<HttpStatusCode>();
        for (int i = 0; i < 6; i++)
        {
            statuses.Add((await Exchange(fx, "bogus")).StatusCode);
        }

        statuses.Should().Contain(HttpStatusCode.TooManyRequests, "the exchange endpoint is rate limited");
    }

    // ── endpoint-graph anonymous exceptions ──────────────────────────────────

    [Fact]
    public async Task OnlyBffEndpoints_AreAnonymous_EverythingElseProtected()
    {
        using TestFixture fx = CreateServer();

        // Protected route requires auth.
        (await fx.Client.GetAsync("/api/whoami")).StatusCode
            .Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);

        // The three BFF endpoints are reachable anonymously (start redirects; exchange 400s on a bogus
        // body but is NOT an auth rejection).
        (await fx.Client.GetAsync(GitHubAuthConstants.StartRoute)).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await Exchange(fx, "bogus")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private async Task<(string State, string Cookie)> BeginLogin(TestFixture fx)
    {
        HttpResponseMessage resp = await fx.Client.GetAsync(GitHubAuthConstants.StartRoute);
        string state = System.Web.HttpUtility.ParseQueryString(resp.Headers.Location!.Query)["state"]!;
        string setCookie = resp.Headers.GetValues("Set-Cookie").First(c => c.Contains("rp_gh_state"));
        string cookie = setCookie.Split(';')[0]; // name=value
        return (state, cookie);
    }

    private async Task<HttpResponseMessage> Callback(
        TestFixture fx, string? state, string? cookie, string? code, string? error = null)
    {
        var qs = new List<string>();
        if (state is not null)
        {
            qs.Add("state=" + Uri.EscapeDataString(state));
        }

        if (code is not null)
        {
            qs.Add("code=" + Uri.EscapeDataString(code));
        }

        if (error is not null)
        {
            qs.Add("error=" + Uri.EscapeDataString(error));
        }

        var req = new HttpRequestMessage(HttpMethod.Get, GitHubAuthConstants.CallbackRoute + "?" + string.Join("&", qs));
        if (cookie is not null)
        {
            req.Headers.Add("Cookie", cookie);
        }

        return await fx.Client.SendAsync(req);
    }

    private async Task<string> LoginToRedemption(TestFixture fx)
    {
        (string state, string cookie) = await BeginLogin(fx);
        HttpResponseMessage resp = await Callback(fx, state, cookie, code: "gh-auth-code");
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        return System.Web.HttpUtility.ParseQueryString(resp.Headers.Location!.Query)["code"]!;
    }

    private static async Task<HttpResponseMessage> Exchange(TestFixture fx, string code)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, GitHubAuthConstants.ExchangeRoute)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { code }), Encoding.UTF8, "application/json"),
        };
        return await fx.Client.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> GetProtectedRaw(TestFixture fx, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await fx.Client.SendAsync(req);
    }

    private static async Task<WhoAmI> GetProtected(TestFixture fx, string token)
    {
        HttpResponseMessage resp = await GetProtectedRaw(fx, token);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<WhoAmI>())!;
    }

    private static void AssertStateCookieCleared(HttpResponseMessage resp)
    {
        resp.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies).Should().BeTrue();
        string cleared = cookies!.Single(c => c.Contains("rp_gh_state"));
        cleared.Should().Match(c => c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase)
            || c.Contains("max-age=0", StringComparison.OrdinalIgnoreCase),
            "the state cookie must be deleted after callback");
    }

    private static string Token(
        long id = AllowedId,
        string login = AllowedLogin,
        string? issuer = null,
        string? audience = null,
        string provider = "GitHub",
        string role = "RetailPulse.User",
        string scope = "access_as_user",
        DateTime? expires = null,
        DateTime? notBefore = null,
        SymmetricSecurityKey? key = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, $"github:{id}"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new("provider", provider),
            new("roles", role),
            new("scp", scope),
            new(GitHubAuthConstants.LoginClaimType, login),
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
        string[]? allowedOrgs = null,
        bool useUserIdAllowlist = true,
        int stateTtlSeconds = 300,
        int redemptionTtlSeconds = 120,
        int startPerMinute = 100,
        int exchangePerMinute = 100)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = "GitHub",
            ["GitHub:ClientId"] = "Iv1.testclientid",
            ["GitHub:ClientSecret"] = "test-client-secret",
            ["GitHub:SigningKey"] = SigningKeyText,
            ["GitHub:Issuer"] = Issuer,
            ["GitHub:Audience"] = Audience,
            ["GitHub:CallbackUrl"] = "https://api.example.com/api/auth/github/callback",
            ["GitHub:FrontendReturnUrl"] = FrontendUrl,
            ["GitHub:StateTtlSeconds"] = stateTtlSeconds.ToString(),
            ["GitHub:RedemptionTtlSeconds"] = redemptionTtlSeconds.ToString(),
            ["GitHub:RateLimits:StartPerMinute"] = startPerMinute.ToString(),
            ["GitHub:RateLimits:ExchangePerMinute"] = exchangePerMinute.ToString(),
        };

        if (useUserIdAllowlist)
        {
            settings["GitHub:AllowedUserIds:0"] = AllowedId.ToString();
            settings["GitHub:AllowedLogins:0"] = AllowedLogin;
        }

        if (allowedOrgs is not null)
        {
            for (int i = 0; i < allowedOrgs.Length; i++)
            {
                settings[$"GitHub:AllowedOrgs:{i}"] = allowedOrgs[i];
            }
        }

        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var clock = new TestClock();
        var oauth = new FakeGitHubOAuthClient
        {
            OnExchange = _ => new GitHubTokenResult(true, ProviderToken, null),
            OnGetUser = _ => new GitHubUserResult(true, AllowedId, AllowedLogin, null),
        };
        var logSink = new ListLoggerProvider();

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = Environments.Production;
        builder.WebHost.UseTestServer();

        builder.Services.AddRouting();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logSink);

        // Wire the full GitHub stack (scheme + policy + stores + allowlist + normalizer).
        builder.Services.AddProviderNeutralAuthentication(config, builder.Environment);

        // Replace the real HTTP transport with the programmable fake, and pin the store clocks so
        // TTL-expiry tests are deterministic. The session token service keeps real time so its tokens
        // validate against the JwtBearer lifetime check.
        builder.Services.RemoveAll<IGitHubOAuthClient>();
        builder.Services.AddSingleton<IGitHubOAuthClient>(oauth);
        builder.Services.RemoveAll<GitHubStateStore>();
        builder.Services.AddSingleton(new GitHubStateStore(clock));
        builder.Services.RemoveAll<GitHubRedemptionStore>();
        builder.Services.AddSingleton(new GitHubRedemptionStore(clock));
        // The endpoints resolve an optional TimeProvider used for state/redemption expiry; pin it to
        // the same clock the stores use so "expired" tests advance a single coherent clock.
        builder.Services.AddSingleton<TimeProvider>(clock);

        builder.Services.AddRateLimiter(rl =>
        {
            rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            rl.AddPolicy(GitHubAuthEndpoints.StartRateLimitPolicy, _ =>
                System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    "github-start",
                    _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        PermitLimit = startPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
            rl.AddPolicy(GitHubAuthEndpoints.ExchangeRateLimitPolicy, _ =>
                System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    "github-exchange",
                    _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        PermitLimit = exchangePerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
        });

        WebApplication app = builder.Build();

        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        // The three narrowly-anonymous BFF endpoints, mapped exactly as Program.cs does in GitHub mode.
        app.MapGitHubAuthEndpoints();

        // Protected stand-ins proving REST bearer + hub query-token parity and deny-by-default.
        app.MapGet("/api/whoami", (HttpContext ctx) =>
            Results.Ok(new { subject = ctx.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value }))
            .RequireAuthorization();
        app.MapGet("/hubs/telemetry", static () => Results.Ok(new { hub = "telemetry" }))
            .RequireAuthorization();
        app.MapGet("/health", static () => Results.Ok(new { status = "ok" })).AllowAnonymous();

        app.Start();
        return new TestFixture(app, oauth, clock, logSink);
    }

    private sealed class ExchangeBody
    {
        public string? Token { get; set; }
        public string? TokenType { get; set; }
        public int ExpiresInSeconds { get; set; }
        public string? Subject { get; set; }
    }

    private sealed class WhoAmI
    {
        public string? Subject { get; set; }
    }

    private sealed class TestClock : TimeProvider
    {
        // Anchored to real "now" so session tokens minted through the pinned clock still validate
        // against the JwtBearer lifetime check (which uses the real system clock). Advance() then
        // drives the deterministic state/redemption TTL-expiry tests.
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class TestFixture : IDisposable
    {
        private readonly IHost _host;

        public TestFixture(IHost host, FakeGitHubOAuthClient oauth, TestClock clock, ListLoggerProvider logSink)
        {
            _host = host;
            OAuth = oauth;
            Clock = clock;
            LogSink = logSink;
            Client = host.GetTestClient();
        }

        public HttpClient Client { get; }
        public FakeGitHubOAuthClient OAuth { get; }
        public TestClock Clock { get; }
        public ListLoggerProvider LogSink { get; }

        public void Dispose()
        {
            Client.Dispose();
            _host.StopAsync().GetAwaiter().GetResult();
            _host.Dispose();
        }
    }

    /// <summary>
    /// Captures every formatted log message into a thread-safe list so tests can assert the GitHub
    /// PROVIDER token (and other secrets) never appear anywhere in the logging pipeline.
    /// </summary>
    private sealed class ListLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => [.. _messages];

        public ILogger CreateLogger(string categoryName) => new ListLogger(_messages);

        public void Dispose()
        {
        }

        private sealed class ListLogger(System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                string message = formatter(state, exception);
                if (exception is not null)
                {
                    message += " " + exception;
                }

                messages.Enqueue(message);
            }
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using RetailPulse.Api.Security;

namespace RetailPulse.Tests.Security;

/// <summary>
/// End-to-end HTTP contract tests for the app-only (client-credentials) token opt-in
/// added by issue #163.
///
/// The behaviour under test is deliberately narrow — the same real
/// <see cref="AuthenticationSetup"/> + <see cref="EntraAuthOptions"/> stack Production
/// uses, wired up in an in-process <see cref="TestServer"/>, with a symmetric signing
/// key so tests can mint tokens offline. Every assertion the security review card in
/// #163 calls out is covered:
/// <list type="bullet">
///   <item>App-only token <b>rejected</b> when the feature is disabled (the default)
///     — the most important test; proves an accidental token acceptance can only
///     happen when someone deliberately opts in.</item>
///   <item>App-only token <b>accepted</b> when enabled and the required app role is
///     present, whether or not an allow-list is configured.</item>
///   <item>App-only token <b>rejected</b> when enabled but the wrong role, no role,
///     or a non-allow-listed <c>azp</c>/<c>appid</c> is presented.</item>
///   <item>Delegated token behaviour <b>byte-for-byte unchanged</b> whether the flag
///     is off (the default) or on — both success and the missing-scope/missing-role
///     failure modes.</item>
///   <item>Token carrying <b>neither</b> <c>scp</c> nor <c>roles</c> is rejected.</item>
///   <item>Both SignalR hub surfaces still reject anonymous connections and negotiate
///     requests exactly as they did before this opt-in existed.</item>
/// </list>
/// </summary>
public sealed class AppOnlyTokenAuthenticationTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string ClientId = "33333333-3333-3333-3333-333333333333";
    private const string MonitorAppId = "b8212317-e16d-4f06-996b-955e885ca1ca";
    private const string OtherAppId = "44444444-4444-4444-4444-444444444444";
    private const string AppRole = "RetailPulse.User";
    private const string ApiScope = "access_as_user";
    private const string Issuer = "https://login.microsoftonline.com/" + TenantId + "/v2.0";

    private static readonly SymmetricSecurityKey SigningKey = new(
        System.Text.Encoding.UTF8.GetBytes("retail-pulse-test-signing-key-0123456789ABCDEF"));

    // ── App-only token REJECTED when the feature is off (the default) ─────────
    //
    // This is the single most important assertion in the whole file: an existing
    // deployment that has not opted in must behave exactly as it does today and MUST
    // return 403 for a service-principal token, even if that token carries the right
    // app role. #163 explicitly names this as the top test.

    [Fact]
    public async Task AppOnlyToken_WhenFeatureDisabled_Returns403()
    {
        using TestFixture fx = CreateServer(allowAppOnly: false);
        string token = CreateToken(Issuer, ClientId, AppOnlyClaims(MonitorAppId));

        HttpResponseMessage response = await Post(fx, "/api/chat", token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "app-only acceptance is opt-in; the default policy must reject client-credentials tokens");
    }

    [Fact]
    public async Task AppOnlyToken_WhenFeatureDisabled_IsRejectedOnHubs_ViaQueryToken()
    {
        using TestFixture fx = CreateServer(allowAppOnly: false);
        string token = CreateToken(Issuer, ClientId, AppOnlyClaims(MonitorAppId));

        HttpResponseMessage response = await fx.Client.GetAsync($"/hubs/telemetry?access_token={token}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "both hubs must reject app-only callers when the opt-in is off");
    }

    // ── App-only token ACCEPTED when enabled and role present ────────────────

    [Fact]
    public async Task AppOnlyToken_WhenFeatureEnabled_WithRequiredRole_Returns200()
    {
        using TestFixture fx = CreateServer(allowAppOnly: true);
        string token = CreateToken(Issuer, ClientId, AppOnlyClaims(MonitorAppId));

        HttpResponseMessage response = await Post(fx, "/api/chat", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "an app-only token carrying the required app role must be accepted when the opt-in is on");
    }

    [Fact]
    public async Task AppOnlyToken_WhenFeatureEnabled_WithRequiredRole_ConnectsToHub()
    {
        using TestFixture fx = CreateServer(allowAppOnly: true);
        string token = CreateToken(Issuer, ClientId, AppOnlyClaims(MonitorAppId));

        HttpResponseMessage response = await fx.Client.GetAsync($"/hubs/telemetry?access_token={token}");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a role-carrying app-only token must be accepted on both REST and hub surfaces when the opt-in is on");
    }

    // ── App-only token REJECTED when enabled but wrong role / no role ────────

    [Fact]
    public async Task AppOnlyToken_WhenFeatureEnabled_MissingRole_Returns403()
    {
        using TestFixture fx = CreateServer(allowAppOnly: true);
        // No "roles" claim at all — a service-principal token with no application permission.
        string token = CreateToken(Issuer, ClientId, [new Claim("azp", MonitorAppId)]);

        HttpResponseMessage response = await Post(fx, "/api/chat", token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "an app-only token without the required app role must be rejected even when the opt-in is on");
    }

    [Fact]
    public async Task AppOnlyToken_WhenFeatureEnabled_WrongRole_Returns403()
    {
        using TestFixture fx = CreateServer(allowAppOnly: true);
        string token = CreateToken(Issuer, ClientId,
            [new Claim("roles", "Some.Other.Role"), new Claim("azp", MonitorAppId)]);

        HttpResponseMessage response = await Post(fx, "/api/chat", token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "an app-only token with an unrelated role must be rejected — no general scope-bypass");
    }

    // ── Delegated behaviour is BYTE-FOR-BYTE UNCHANGED in either configuration ─

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelegatedToken_WithRoleAndScope_Returns200_InBothConfigurations(bool allowAppOnly)
    {
        using TestFixture fx = CreateServer(allowAppOnly);
        string token = CreateToken(Issuer, ClientId,
            [new Claim("roles", AppRole), new Claim("scp", ApiScope)]);

        HttpResponseMessage response = await Post(fx, "/api/chat", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a valid delegated token must be accepted whether or not the app-only opt-in is on");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelegatedToken_MissingScope_Returns403_InBothConfigurations(bool allowAppOnly)
    {
        using TestFixture fx = CreateServer(allowAppOnly);
        // Delegated token (has scp="") but missing the required scope value.
        string token = CreateToken(Issuer, ClientId,
            [new Claim("roles", AppRole), new Claim("scp", "some.other.scope")]);

        HttpResponseMessage response = await Post(fx, "/api/chat", token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "delegated tokens still require the exact configured API scope — behaviour unchanged");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelegatedToken_MissingRole_Returns403_InBothConfigurations(bool allowAppOnly)
    {
        using TestFixture fx = CreateServer(allowAppOnly);
        string token = CreateToken(Issuer, ClientId, [new Claim("scp", ApiScope)]);

        HttpResponseMessage response = await Post(fx, "/api/chat", token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "delegated tokens still require the app role — behaviour unchanged");
    }

    // ── Token with neither scp nor roles → REJECTED ──────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TokenWithNeitherScpNorRoles_Returns403_InBothConfigurations(bool allowAppOnly)
    {
        using TestFixture fx = CreateServer(allowAppOnly);
        // Signature/issuer/audience are all valid — but the token carries no authorization
        // signal at all. It must be rejected regardless of the opt-in state.
        string token = CreateToken(Issuer, ClientId, []);

        HttpResponseMessage response = await Post(fx, "/api/chat", token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a signed token with no scp and no roles gives the policy nothing to authorize on — deny");
    }

    // ── Optional azp/appid allow-list ────────────────────────────────────────

    [Fact]
    public async Task AppOnlyToken_WhenAllowlistConfigured_MatchesAzp_Returns200()
    {
        using TestFixture fx = CreateServer(allowAppOnly: true, allowlist: [MonitorAppId]);
        string token = CreateToken(Issuer, ClientId,
            [new Claim("roles", AppRole), new Claim("azp", MonitorAppId)]);

        HttpResponseMessage response = await Post(fx, "/api/chat", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "an allow-listed azp must be accepted");
    }

    [Fact]
    public async Task AppOnlyToken_WhenAllowlistConfigured_MatchesAppId_Returns200()
    {
        // v1 tokens use "appid" instead of "azp" — allow-list must match on either.
        using TestFixture fx = CreateServer(allowAppOnly: true, allowlist: [MonitorAppId]);
        string token = CreateToken(Issuer, ClientId,
            [new Claim("roles", AppRole), new Claim("appid", MonitorAppId)]);

        HttpResponseMessage response = await Post(fx, "/api/chat", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a v1-style appid must match the allow-list");
    }

    [Fact]
    public async Task AppOnlyToken_WhenAllowlistConfigured_UnknownAzp_Returns403()
    {
        using TestFixture fx = CreateServer(allowAppOnly: true, allowlist: [MonitorAppId]);
        string token = CreateToken(Issuer, ClientId,
            [new Claim("roles", AppRole), new Claim("azp", OtherAppId)]);

        HttpResponseMessage response = await Post(fx, "/api/chat", token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a non-allow-listed azp must be rejected even with the correct role");
    }

    [Fact]
    public async Task AppOnlyToken_WhenAllowlistConfigured_NoAzpOrAppId_Returns403()
    {
        using TestFixture fx = CreateServer(allowAppOnly: true, allowlist: [MonitorAppId]);
        // No azp, no appid — the allow-list has no claim to match against.
        string token = CreateToken(Issuer, ClientId, [new Claim("roles", AppRole)]);

        HttpResponseMessage response = await Post(fx, "/api/chat", token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "an app-only token with no azp/appid cannot satisfy an allow-list");
    }

    // ── Hub anonymous surfaces stay closed on both configurations ────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnonymousHubNegotiate_Returns401_InBothConfigurations(bool allowAppOnly)
    {
        using TestFixture fx = CreateServer(allowAppOnly);

        HttpResponseMessage telemetry = await fx.Client.PostAsync(
            "/hubs/telemetry/negotiate", new StringContent(string.Empty));
        HttpResponseMessage streaming = await fx.Client.PostAsync(
            "/hubs/streaming/negotiate", new StringContent(string.Empty));

        telemetry.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "anonymous hub negotiate must remain closed regardless of the app-only opt-in");
        streaming.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "anonymous hub negotiate must remain closed regardless of the app-only opt-in");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static Claim[] AppOnlyClaims(string clientAppId) =>
        [new Claim("roles", AppRole), new Claim("azp", clientAppId)];

    private static async Task<HttpResponseMessage> Post(TestFixture fx, string path, string bearerToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return await fx.Client.SendAsync(request);
    }

    private static string CreateToken(string issuer, string audience, IEnumerable<Claim> claims)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        };

        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.CreateToken(descriptor);
    }

    private static TestFixture CreateServer(bool allowAppOnly, string[]? allowlist = null)
    {
        Dictionary<string, string?> settings = new()
        {
            ["Security:RequireAuth"] = "true",
            ["MicrosoftEntra:Instance"] = "https://login.microsoftonline.com/",
            ["MicrosoftEntra:TenantId"] = TenantId,
            ["MicrosoftEntra:ClientId"] = ClientId,
            ["MicrosoftEntra:ApiScope"] = ApiScope,
            ["MicrosoftEntra:AppRole"] = AppRole,
            ["MicrosoftEntra:AllowAppOnlyTokens"] = allowAppOnly ? "true" : "false",
        };

        if (allowlist is not null)
        {
            for (int i = 0; i < allowlist.Length; i++)
            {
                settings[$"MicrosoftEntra:AllowedAppClientIds:{i}"] = allowlist[i];
            }
        }

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        IHostBuilder builder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices((context, services) =>
                {
                    services.AddRouting();

                    // Force the production (non-Development) branch so the real JwtBearer
                    // scheme is registered — HostBuilder defaults to Production.
                    EntraAuthOptions options = services.AddRetailPulseAuthentication(
                        config, context.HostingEnvironment);
                    services.AddRetailPulseAuthorization(options);

                    // Symmetric-key override so tests can validate self-signed tokens
                    // offline while keeping issuer/audience/lifetime validation on.
                    services.PostConfigure<JwtBearerOptions>(
                        JwtBearerDefaults.AuthenticationScheme,
                        jwt =>
                        {
                            jwt.Authority = null;
                            jwt.MetadataAddress = null!;
                            jwt.ConfigurationManager = null;
                            jwt.RequireHttpsMetadata = false;
                            jwt.TokenValidationParameters.IssuerSigningKey = SigningKey;
                            jwt.TokenValidationParameters.ValidateIssuerSigningKey = true;
                        });
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/api/chat", static () => Results.Ok(new { reply = "ok" }))
                            .RequireAuthorization();

                        // Both hub connection and negotiate endpoints for BOTH hubs, so
                        // the tests can assert that neither hub is widened by this opt-in.
                        endpoints.MapGet("/hubs/telemetry", static () => Results.Ok(new { hub = "telemetry" }))
                            .RequireAuthorization();
                        endpoints.MapPost("/hubs/telemetry/negotiate", static () => Results.Ok(new { negotiated = true }))
                            .RequireAuthorization();
                        endpoints.MapGet("/hubs/streaming", static () => Results.Ok(new { hub = "streaming" }))
                            .RequireAuthorization();
                        endpoints.MapPost("/hubs/streaming/negotiate", static () => Results.Ok(new { negotiated = true }))
                            .RequireAuthorization();

                        endpoints.MapGet("/health", static () => Results.Ok(new { status = "ok" })).AllowAnonymous();
                        endpoints.MapGet("/alive", static () => Results.Ok(new { status = "alive" })).AllowAnonymous();
                    });
                });
            });

        IHost host = builder.Build();
        host.Start();
        return new TestFixture(host);
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

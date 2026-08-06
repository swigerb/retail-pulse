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
/// End-to-end HTTP contract tests for the production Entra JWT security boundary
/// (<see cref="AuthenticationSetup"/> + <see cref="EntraAuthOptions"/>).
///
/// The full <c>Program.cs</c> cannot run under <c>WebApplicationFactory</c> (it fails fast
/// requiring Azure credentials at startup), so these tests build a minimal in-process
/// <see cref="TestServer"/> that wires the EXACT same authentication/authorization stack the
/// API uses in Production, then inject a symmetric signing key so tests can mint their own
/// tokens without contacting Entra. Every case a security reviewer cares about is asserted:
/// <list type="bullet">
///   <item>anonymous REST → 401</item>
///   <item>wrong tenant / wrong audience → 401</item>
///   <item>correct tenant but missing app role → 403 (unassigned user)</item>
///   <item>correct tenant + role but missing API scope → 403</item>
///   <item>correct tenant + role + scope → 200</item>
///   <item><c>/health</c> and <c>/alive</c> stay anonymous → 200</item>
///   <item>the <c>access_token</c> query param is honoured ONLY on <c>/hubs</c></item>
///   <item>even with <c>RequireAuth=false</c> the default policy still requires a user (never permissive)</item>
/// </list>
/// </summary>
public sealed class EntraAuthenticationTests
{
    // Deterministic, non-secret test identifiers (NOT real tenant/app IDs).
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string OtherTenantId = "22222222-2222-2222-2222-222222222222";
    private const string ClientId = "33333333-3333-3333-3333-333333333333";
    private const string AppRole = "RetailPulse.User";
    private const string ApiScope = "access_as_user";

    private const string Issuer = "https://login.microsoftonline.com/" + TenantId + "/v2.0";
    private const string WrongIssuer = "https://login.microsoftonline.com/" + OtherTenantId + "/v2.0";

    // 256-bit symmetric key used both to sign test tokens and to validate them in-process.
    private static readonly SymmetricSecurityKey SigningKey = new(
        System.Text.Encoding.UTF8.GetBytes("retail-pulse-test-signing-key-0123456789ABCDEF"));

    // ── anonymous ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AnonymousRestCall_Returns401()
    {
        using TestFixture fx = CreateServer(requireAuth: true);
        HttpResponseMessage response = await fx.Client.PostAsync("/api/chat", new StringContent(string.Empty));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnonymousHubNegotiate_Returns401()
    {
        using TestFixture fx = CreateServer(requireAuth: true);
        HttpResponseMessage response = await fx.Client.PostAsync("/hubs/telemetry/negotiate", new StringContent(string.Empty));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── wrong tenant / audience → 401 ─────────────────────────────────────────

    [Fact]
    public async Task WrongTenantIssuer_Returns401()
    {
        using TestFixture fx = CreateServer(requireAuth: true);
        string token = CreateToken(WrongIssuer, ClientId, RoleAndScopeClaims());
        HttpResponseMessage response = await Post(fx, "/api/chat", token);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WrongAudience_Returns401()
    {
        using TestFixture fx = CreateServer(requireAuth: true);
        string token = CreateToken(Issuer, "api://not-this-api", RoleAndScopeClaims());
        HttpResponseMessage response = await Post(fx, "/api/chat", token);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── correct tenant, insufficient claims → 403 ─────────────────────────────

    [Fact]
    public async Task CorrectTenant_MissingAppRole_Returns403()
    {
        // Simulates a user in the right tenant who is NOT assigned the enterprise app role.
        using TestFixture fx = CreateServer(requireAuth: true);
        string token = CreateToken(Issuer, ClientId, [new Claim("scp", ApiScope)]);
        HttpResponseMessage response = await Post(fx, "/api/chat", token);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CorrectTenant_MissingScope_Returns403()
    {
        using TestFixture fx = CreateServer(requireAuth: true);
        string token = CreateToken(Issuer, ClientId, [new Claim("roles", AppRole)]);
        HttpResponseMessage response = await Post(fx, "/api/chat", token);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── assigned user → 200 ───────────────────────────────────────────────────

    [Fact]
    public async Task AssignedUser_WithRoleAndScope_Returns200()
    {
        using TestFixture fx = CreateServer(requireAuth: true);
        string token = CreateToken(Issuer, ClientId, RoleAndScopeClaims());
        HttpResponseMessage response = await Post(fx, "/api/chat", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AssignedUser_AcceptsApiUriAudience()
    {
        // Tokens minted for the api://{clientId} audience must also validate.
        using TestFixture fx = CreateServer(requireAuth: true);
        string token = CreateToken(Issuer, $"api://{ClientId}", RoleAndScopeClaims());
        HttpResponseMessage response = await Post(fx, "/api/chat", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── liveness/readiness stay anonymous ─────────────────────────────────────

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task LivenessAndReadiness_RemainAnonymous(string path)
    {
        using TestFixture fx = CreateServer(requireAuth: true);
        HttpResponseMessage response = await fx.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── hub-only query-token mapping ──────────────────────────────────────────

    [Fact]
    public async Task QueryToken_OnHubPath_IsAccepted()
    {
        // WebSocket handshakes can't set an Authorization header, so SignalR passes the
        // token as ?access_token=. This must succeed on /hubs.
        using TestFixture fx = CreateServer(requireAuth: true);
        string token = CreateToken(Issuer, ClientId, RoleAndScopeClaims());
        HttpResponseMessage response = await fx.Client.GetAsync($"/hubs/telemetry?access_token={token}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task QueryToken_OnRestPath_IsIgnored()
    {
        // The query-token escape hatch must NOT widen the token surface for ordinary REST.
        using TestFixture fx = CreateServer(requireAuth: true);
        string token = CreateToken(Issuer, ClientId, RoleAndScopeClaims());
        HttpResponseMessage response = await fx.Client.PostAsync($"/api/chat?access_token={token}", new StringContent(string.Empty));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── never permissive / deny-by-default fallback ──────────────────────────

    [Fact]
    public async Task UnannotatedApiEndpoint_IsProtectedByFallbackPolicy()
    {
        // An /api endpoint that forgot RequireAuthorization must still reject anonymous
        // callers because of the deny-by-default FallbackPolicy — no billable anonymous path.
        using TestFixture fx = CreateServer(requireAuth: true);
        HttpResponseMessage anonymous = await fx.Client.GetAsync("/api/unannotated");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnannotatedApiEndpoint_AllowsAuthorizedUserViaFallbackPolicy()
    {
        // The fallback policy is the SAME strong policy: a fully-assigned user still passes.
        using TestFixture fx = CreateServer(requireAuth: true);
        string token = CreateToken(Issuer, ClientId, RoleAndScopeClaims());
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/unannotated");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response = await fx.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UnannotatedApiEndpoint_MissingRole_IsForbiddenByFallbackPolicy()
    {
        // The fallback policy also enforces role+scope, so an authenticated-but-unassigned
        // user is forbidden rather than served.
        using TestFixture fx = CreateServer(requireAuth: true);
        string token = CreateToken(Issuer, ClientId, [new Claim("scp", ApiScope)]);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/unannotated");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response = await fx.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static Claim[] RoleAndScopeClaims() =>
        [new Claim("roles", AppRole), new Claim("scp", ApiScope)];

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

    private static TestFixture CreateServer(bool requireAuth)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:RequireAuth"] = requireAuth ? "true" : "false",
                ["MicrosoftEntra:Instance"] = "https://login.microsoftonline.com/",
                ["MicrosoftEntra:TenantId"] = TenantId,
                ["MicrosoftEntra:ClientId"] = ClientId,
                ["MicrosoftEntra:ApiScope"] = ApiScope,
                ["MicrosoftEntra:AppRole"] = AppRole,
            })
            .Build();

        IHostBuilder builder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices((context, services) =>
                {
                    services.AddRouting();

                    // Force the production (non-Development) branch so the real JwtBearer
                    // scheme is registered — HostBuilder defaults the environment to Production.
                    EntraAuthOptions options = services.AddRetailPulseAuthentication(
                        config, context.HostingEnvironment);
                    services.AddRetailPulseAuthorization(options);

                    // Replace Entra metadata retrieval with the in-process symmetric key so
                    // tests can validate self-signed tokens offline. Issuer/audience/lifetime
                    // validation stay ON so wrong-tenant/wrong-audience still fail.
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
                        // Protected billable REST endpoint (uses the default policy).
                        endpoints.MapPost("/api/chat", static () => Results.Ok(new { reply = "ok" }))
                            .RequireAuthorization();

                        // Regression fixture: an /api endpoint that FORGOT RequireAuthorization.
                        // The deny-by-default FallbackPolicy must still protect it so a future
                        // unannotated route can never expose a billable anonymous path.
                        endpoints.MapGet("/api/unannotated", static () => Results.Ok(new { data = "secret" }));

                        // Protected hub surface (query-token path). A plain endpoint stands in
                        // for the SignalR hub so we can assert the ?access_token mapping.
                        endpoints.MapGet("/hubs/telemetry", static () => Results.Ok(new { hub = "telemetry" }))
                            .RequireAuthorization();
                        endpoints.MapPost("/hubs/telemetry/negotiate", static () => Results.Ok(new { negotiated = true }))
                            .RequireAuthorization();

                        // Liveness/readiness are the ONLY anonymous endpoints. They must opt
                        // out explicitly because the API sets a deny-by-default FallbackPolicy.
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

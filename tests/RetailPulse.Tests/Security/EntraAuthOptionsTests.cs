using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using RetailPulse.Api.Security;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Fail-closed configuration contract for <see cref="EntraAuthOptions.FromConfiguration"/>.
///
/// These are pure options-resolution tests (no HTTP) proving the API can NEVER boot into a
/// fail-open posture outside Development:
/// <list type="bullet">
///   <item>Production with <c>Security:RequireAuth=false</c> fails startup.</item>
///   <item>Production missing tenant / audience / client fails startup.</item>
///   <item>Production with documentation placeholders (<c>&lt;your-tenant-id&gt;</c>) fails startup.</item>
///   <item>Real <c>MicrosoftEntra</c> values win precedence over legacy <c>Security:*</c> placeholders.</item>
///   <item>Development stays usable (auth may be relaxed and never throws).</item>
/// </list>
/// </summary>
public sealed class EntraAuthOptionsTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string ClientId = "33333333-3333-3333-3333-333333333333";

    private static IConfiguration Config(params (string Key, string? Value)[] entries)
    {
        Dictionary<string, string?> dict = entries.ToDictionary(e => e.Key, e => e.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static IHostEnvironment Env(string name) => new TestHostEnvironment { EnvironmentName = name };

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "RetailPulse.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    // ── Production must never accept RequireAuth=false ────────────────────────

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("ProductionLike")]
    public void RequireAuthFalse_OutsideDevelopment_ThrowsAtStartup(string environment)
    {
        IConfiguration config = Config(
            ("Security:RequireAuth", "false"),
            ("MicrosoftEntra:TenantId", TenantId),
            ("MicrosoftEntra:ClientId", ClientId));

        Action act = () => EntraAuthOptions.FromConfiguration(config, Env(environment));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*RequireAuth=false*not permitted*");
    }

    // ── Production requires tenant + audience/client ──────────────────────────

    [Fact]
    public void Production_MissingTenant_ThrowsAtStartup()
    {
        IConfiguration config = Config(("MicrosoftEntra:ClientId", ClientId));

        Action act = () => EntraAuthOptions.FromConfiguration(config, Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*TenantId*");
    }

    [Fact]
    public void Production_MissingAudienceAndClient_ThrowsAtStartup()
    {
        IConfiguration config = Config(("MicrosoftEntra:TenantId", TenantId));

        Action act = () => EntraAuthOptions.FromConfiguration(config, Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*ClientId*");
    }

    // ── Placeholders that "look valid" must be rejected ───────────────────────

    [Fact]
    public void Production_PlaceholderTenant_ThrowsAtStartup()
    {
        IConfiguration config = Config(
            ("MicrosoftEntra:TenantId", "<your-tenant-id>"),
            ("MicrosoftEntra:ClientId", ClientId));

        Action act = () => EntraAuthOptions.FromConfiguration(config, Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*TenantId*");
    }

    [Fact]
    public void Production_PlaceholderAudience_ThrowsAtStartup()
    {
        IConfiguration config = Config(
            ("MicrosoftEntra:TenantId", TenantId),
            ("MicrosoftEntra:Audience", "<your-api-audience>"));

        Action act = () => EntraAuthOptions.FromConfiguration(config, Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*ClientId*");
    }

    [Fact]
    public void Production_LegacyPlaceholderAuthorityAndAudience_ThrowsAtStartup()
    {
        // The shipped appsettings.Production.json documents these with angle-bracket
        // placeholders; they must NOT satisfy validation.
        IConfiguration config = Config(
            ("Security:JwtAuthority", "https://login.microsoftonline.com/<your-tenant-id>/v2.0"),
            ("Security:JwtAudience", "<your-api-audience>"));

        Action act = () => EntraAuthOptions.FromConfiguration(config, Env("Production"));

        act.Should().Throw<InvalidOperationException>();
    }

    // ── Config precedence ─────────────────────────────────────────────────────

    [Fact]
    public void RealMicrosoftEntraValues_WinOverLegacyPlaceholders()
    {
        // A real MicrosoftEntra client id must win even when a legacy placeholder audience
        // is present — the placeholder never poisons the accepted audience list.
        IConfiguration config = Config(
            ("MicrosoftEntra:TenantId", TenantId),
            ("MicrosoftEntra:ClientId", ClientId),
            ("Security:JwtAuthority", "https://login.microsoftonline.com/<your-tenant-id>/v2.0"),
            ("Security:JwtAudience", "<your-api-audience>"));

        var options = EntraAuthOptions.FromConfiguration(config, Env("Production"));

        options.TenantId.Should().Be(TenantId);
        options.ValidAudiences.Should().BeEquivalentTo([ClientId, $"api://{ClientId}"]);
        options.ValidAudiences.Should().NotContain(a => a.Contains('<'));
    }

    [Fact]
    public void RealMicrosoftEntraAudience_WinsOverLegacyRealAudience()
    {
        IConfiguration config = Config(
            ("MicrosoftEntra:TenantId", TenantId),
            ("MicrosoftEntra:Audience", "api://real-audience"),
            ("Security:JwtAudience", "api://legacy-audience"));

        var options = EntraAuthOptions.FromConfiguration(config, Env("Production"));

        options.ValidAudiences.Should().ContainSingle().Which.Should().Be("api://real-audience");
    }

    [Fact]
    public void Production_ValidConfiguration_ResolvesWithRequireAuth()
    {
        IConfiguration config = Config(
            ("MicrosoftEntra:TenantId", TenantId),
            ("MicrosoftEntra:ClientId", ClientId));

        var options = EntraAuthOptions.FromConfiguration(config, Env("Production"));

        options.RequireAuth.Should().BeTrue();
        options.ValidIssuers.Should().Contain($"https://login.microsoftonline.com/{TenantId}/v2.0");
    }

    // ── Development stays usable ───────────────────────────────────────────────

    [Fact]
    public void Development_MissingEverything_DoesNotThrow_AndRelaxesAuth()
    {
        IConfiguration config = Config();

        var options = EntraAuthOptions.FromConfiguration(config, Env("Development"));

        options.RequireAuth.Should().BeFalse();
    }

    [Fact]
    public void Development_ExplicitRequireAuthFalse_IsAllowed()
    {
        IConfiguration config = Config(("Security:RequireAuth", "false"));

        Action act = () => EntraAuthOptions.FromConfiguration(config, Env("Development"));

        act.Should().NotThrow();
    }

    [Fact]
    public void Development_WithRealConfig_CanRequireAuth()
    {
        IConfiguration config = Config(
            ("Security:RequireAuth", "true"),
            ("MicrosoftEntra:TenantId", TenantId),
            ("MicrosoftEntra:ClientId", ClientId));

        var options = EntraAuthOptions.FromConfiguration(config, Env("Development"));

        options.RequireAuth.Should().BeTrue();
    }

    // ── The default/fallback authorization policy never degrades ──────────────

    [Fact]
    public void UserPolicy_WhenAuthRequired_DemandsRoleAndScope()
    {
        var options = new EntraAuthOptions { RequireAuth = true, TenantId = TenantId, ClientId = ClientId };

        AuthorizationPolicy policy = AuthenticationSetup.BuildUserPolicy(options);

        // RequireAuthenticatedUser + RequireRole + a scope assertion → at least 3 requirements.
        policy.Requirements.Should().HaveCountGreaterThanOrEqualTo(3);
    }
}

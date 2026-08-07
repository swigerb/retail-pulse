using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetailPulse.Api.Auth;
using RetailPulse.Api.Security;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Contract tests for the provider-neutral authentication foundation (Sprint 0):
/// <see cref="AuthenticationModeOptions.Resolve"/> and the
/// <see cref="ProviderNeutralAuthentication.AddProviderNeutralAuthentication"/> factory boundary.
///
/// These prove the deterministic, fail-closed selection rules:
/// <list type="bullet">
///   <item>Explicit <c>Entra</c> (any casing) resolves and wires the existing Entra boundary.</item>
///   <item>GitHub (Sprint 2) resolves as a known mode and wires its own confidential OAuth BFF
///     session stack; it fails startup fail-closed without a complete, validated configuration
///     (client id + secret + signing key + exact HTTPS URLs + a non-empty allowlist) and never
///     falls through to Entra/dev/anonymous.</item>
///   <item>Anonymous (Sprint 1) wires its own constrained session stack; hosted use fails
///     closed without the second opt-in + signing key + daily ceilings.</item>
///   <item>Unknown/malformed/numeric modes fail startup.</item>
///   <item>Production with a missing mode fails closed; Development defaults to Entra.</item>
/// </list>
/// </summary>
public sealed class AuthenticationModeTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string ClientId = "33333333-3333-3333-3333-333333333333";

    private static IConfiguration Config(params (string Key, string? Value)[] entries)
    {
        Dictionary<string, string?> dict = entries.ToDictionary(e => e.Key, e => e.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static IConfiguration EntraConfig(string? mode)
    {
        var entries = new List<(string, string?)>
        {
            ("MicrosoftEntra:TenantId", TenantId),
            ("MicrosoftEntra:ClientId", ClientId),
        };
        if (mode is not null)
        {
            entries.Add(("Authentication:Mode", mode));
        }

        return Config([.. entries]);
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

    // ── Resolve: explicit known modes ─────────────────────────────────────────

    [Theory]
    [InlineData("Entra", AuthenticationMode.Entra)]
    [InlineData("entra", AuthenticationMode.Entra)]
    [InlineData("ENTRA", AuthenticationMode.Entra)]
    [InlineData("GitHub", AuthenticationMode.GitHub)]
    [InlineData("github", AuthenticationMode.GitHub)]
    [InlineData("Anonymous", AuthenticationMode.Anonymous)]
    [InlineData("anonymous", AuthenticationMode.Anonymous)]
    public void Resolve_ExplicitKnownMode_IsHonouredCaseInsensitively(string raw, AuthenticationMode expected)
    {
        IConfiguration config = Config(("Authentication:Mode", raw));

        AuthenticationMode mode = AuthenticationModeOptions.Resolve(config, Env("Production"));

        mode.Should().Be(expected);
    }

    // ── Resolve: missing mode is environment-dependent ────────────────────────

    [Fact]
    public void Resolve_MissingMode_InDevelopment_DefaultsToEntra()
    {
        AuthenticationMode mode = AuthenticationModeOptions.Resolve(Config(), Env("Development"));

        mode.Should().Be(AuthenticationMode.Entra);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("ProductionLike")]
    public void Resolve_MissingMode_OutsideDevelopment_FailsClosed(string environment)
    {
        Action act = () => AuthenticationModeOptions.Resolve(Config(), Env(environment));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Authentication:Mode is not configured*fails closed*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankMode_OutsideDevelopment_FailsClosed(string? raw)
    {
        IConfiguration config = raw is null ? Config() : Config(("Authentication:Mode", raw));

        Action act = () => AuthenticationModeOptions.Resolve(config, Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*not configured*");
    }

    // ── Resolve: unknown / malformed / numeric modes fail ─────────────────────

    [Theory]
    [InlineData("Foo")]
    [InlineData("Ldap")]
    [InlineData("En tra")]
    [InlineData("Entra ")] // trailing space is trimmed → still Entra, so NOT here
    public void Resolve_UnknownMode_FailsClosed(string raw)
    {
        // "Entra " trims to a valid mode, so only assert failure for genuinely unknown values.
        if (raw.Trim().Equals("Entra", StringComparison.OrdinalIgnoreCase))
        {
            AuthenticationModeOptions.Resolve(Config(("Authentication:Mode", raw)), Env("Production"))
                .Should().Be(AuthenticationMode.Entra);
            return;
        }

        Action act = () => AuthenticationModeOptions.Resolve(Config(("Authentication:Mode", raw)), Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*not a recognized authentication mode*");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    public void Resolve_NumericMode_IsRejected(string raw)
    {
        // A bare integer must never select a mode — modes are documented names only.
        Action act = () => AuthenticationModeOptions.Resolve(Config(("Authentication:Mode", raw)), Env("Development"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*not a recognized authentication mode*");
    }

    // ── Factory boundary: Entra wires the existing stack ──────────────────────

    [Fact]
    public void AddProviderNeutralAuthentication_Entra_WiresEntraStackAndNormalizer()
    {
        var services = new ServiceCollection();

        EntraAuthOptions? options = services.AddProviderNeutralAuthentication(
            EntraConfig("Entra"), Env("Production"));

        options.Should().NotBeNull();
        options!.RequireAuth.Should().BeTrue("Production must run real Entra auth");

        ServiceProvider provider = services.BuildServiceProvider();
        provider.GetService<EntraAuthOptions>().Should().NotBeNull();
        provider.GetService<AuthenticationModeOptions>()!.Mode.Should().Be(AuthenticationMode.Entra);

        IPrincipalNormalizer normalizer = provider.GetRequiredService<IPrincipalNormalizer>();
        normalizer.Should().BeOfType<EntraPrincipalNormalizer>();
        normalizer.Mode.Should().Be(AuthenticationMode.Entra);
    }

    [Fact]
    public void AddProviderNeutralAuthentication_Entra_MissingModeInDevelopment_DefaultsToEntra()
    {
        var services = new ServiceCollection();

        EntraAuthOptions? options = services.AddProviderNeutralAuthentication(
            Config(), Env("Development"));

        options.Should().NotBeNull();
        services.BuildServiceProvider().GetService<IPrincipalNormalizer>().Should().NotBeNull();
    }

    // ── Factory boundary: GitHub mode (Sprint 2) ─────────────────────────────

    private const string GitHubSigningKey = "github-mode-test-signing-key-0123456789abcdef";

    private static IConfiguration GitHubConfig(string environment, params (string Key, string? Value)[] extra)
    {
        var entries = new List<(string, string?)>
        {
            ("Authentication:Mode", "GitHub"),
            ("GitHub:ClientId", "Iv1.abcdef0123456789"),
            ("GitHub:ClientSecret", "test-github-client-secret-value"),
            ("GitHub:CallbackUrl", "https://api.example.com/api/auth/github/callback"),
            ("GitHub:FrontendReturnUrl", "https://app.example.com/auth/github/callback"),
            ("GitHub:AllowedUserIds:0", "12345"),
        };
        if (!environment.Equals("Development", StringComparison.OrdinalIgnoreCase))
        {
            entries.Add(("GitHub:SigningKey", GitHubSigningKey));
            entries.Add(("GitHub:RequireSecureCookies", "true"));
            entries.Add(("GitHub:AcknowledgeSingleReplica", "true"));
        }

        entries.AddRange(extra);
        return Config([.. entries]);
    }

    [Fact]
    public void AddProviderNeutralAuthentication_HostedGitHubFullyConfigured_WiresGitHubStackAndReturnsNull()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        EntraAuthOptions? options = services.AddProviderNeutralAuthentication(
            GitHubConfig("Production"), Env("Production"));

        // GitHub wires its own authorization internally, so it returns null (Program.cs must NOT
        // layer the Entra policy on top).
        options.Should().BeNull();

        ServiceProvider provider = services.BuildServiceProvider();
        provider.GetService<AuthenticationModeOptions>()!.Mode.Should().Be(AuthenticationMode.GitHub);

        // The GitHub principal normalizer, options, and session token service are registered.
        IPrincipalNormalizer normalizer = provider.GetRequiredService<IPrincipalNormalizer>();
        normalizer.Mode.Should().Be(AuthenticationMode.GitHub);
        normalizer.Should().BeOfType<GitHubPrincipalNormalizer>();

        Api.Security.GitHub.GitHubAuthOptions resolved =
            provider.GetRequiredService<Api.Security.GitHub.GitHubAuthOptions>();
        resolved.HasConfiguredSigningKey.Should().BeTrue("hosted GitHub requires a configured signing key");

        provider.GetService<Api.Security.GitHub.IGitHubSessionTokenService>().Should().NotBeNull();
        provider.GetService<Api.Security.GitHub.GitHubStateStore>().Should().NotBeNull();
        provider.GetService<Api.Security.GitHub.GitHubRedemptionStore>().Should().NotBeNull();
    }

    [Fact]
    public void AddProviderNeutralAuthentication_GitHubInDevelopment_WiresWithEphemeralKey()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Development may run the real OAuth flow with an ephemeral signing key (no GitHub:SigningKey),
        // but STILL requires the real OAuth credentials, exact URLs, and an allowlist.
        EntraAuthOptions? options = services.AddProviderNeutralAuthentication(
            GitHubConfig("Development"), Env("Development"));

        options.Should().BeNull();

        ServiceProvider provider = services.BuildServiceProvider();
        provider.GetService<AuthenticationModeOptions>()!.Mode.Should().Be(AuthenticationMode.GitHub);
        Api.Security.GitHub.GitHubSigningKeyProvider keyProvider =
            provider.GetRequiredService<Api.Security.GitHub.GitHubSigningKeyProvider>();
        keyProvider.IsEphemeral.Should().BeTrue("Development GitHub mode generates an ephemeral signing key");
    }

    [Fact]
    public void AddProviderNeutralAuthentication_HostedGitHubWithoutSigningKey_FailsClosed()
    {
        var services = new ServiceCollection();

        // Config WITHOUT GitHub:SigningKey but Production → must fail closed.
        var entries = new List<(string, string?)>
        {
            ("Authentication:Mode", "GitHub"),
            ("GitHub:ClientId", "Iv1.abcdef0123456789"),
            ("GitHub:ClientSecret", "test-github-client-secret-value"),
            ("GitHub:CallbackUrl", "https://api.example.com/api/auth/github/callback"),
            ("GitHub:FrontendReturnUrl", "https://app.example.com/auth/github/callback"),
            ("GitHub:AllowedUserIds:0", "12345"),
        };

        Action act = () => services.AddProviderNeutralAuthentication(Config([.. entries]), Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*SigningKey*");
    }

    [Fact]
    public void AddProviderNeutralAuthentication_GitHubWithoutClientSecret_FailsClosed()
    {
        var services = new ServiceCollection();

        var entries = new List<(string, string?)>
        {
            ("Authentication:Mode", "GitHub"),
            ("GitHub:ClientId", "Iv1.abcdef0123456789"),
            ("GitHub:CallbackUrl", "https://api.example.com/api/auth/github/callback"),
            ("GitHub:FrontendReturnUrl", "https://app.example.com/auth/github/callback"),
            ("GitHub:AllowedUserIds:0", "12345"),
        };

        // Even Development requires the OAuth client secret to talk to GitHub.
        Action act = () => services.AddProviderNeutralAuthentication(Config([.. entries]), Env("Development"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*ClientSecret*");
    }

    [Fact]
    public void AddProviderNeutralAuthentication_GitHubWithoutAllowlist_FailsClosed()
    {
        var services = new ServiceCollection();

        var entries = new List<(string, string?)>
        {
            ("Authentication:Mode", "GitHub"),
            ("GitHub:ClientId", "Iv1.abcdef0123456789"),
            ("GitHub:ClientSecret", "test-github-client-secret-value"),
            ("GitHub:SigningKey", GitHubSigningKey),
            ("GitHub:CallbackUrl", "https://api.example.com/api/auth/github/callback"),
            ("GitHub:FrontendReturnUrl", "https://app.example.com/auth/github/callback"),
        };

        Action act = () => services.AddProviderNeutralAuthentication(Config([.. entries]), Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*allowlist*");
    }

    [Fact]
    public void AddProviderNeutralAuthentication_GitHub_PlaceholderValues_FailClosed()
    {
        var services = new ServiceCollection();

        // Copying the example config verbatim (angle-bracket placeholders) must never authenticate.
        var entries = new List<(string, string?)>
        {
            ("Authentication:Mode", "GitHub"),
            ("GitHub:ClientId", "<github-oauth-app-client-id>"),
            ("GitHub:ClientSecret", "<set-via-secret-store>"),
            ("GitHub:SigningKey", "<set-via-secret-store-min-32-bytes>"),
            ("GitHub:CallbackUrl", "https://api.example.com/api/auth/github/callback"),
            ("GitHub:FrontendReturnUrl", "https://app.example.com/auth/github/callback"),
            ("GitHub:AllowedUserIds:0", "12345"),
        };

        Action act = () => services.AddProviderNeutralAuthentication(Config([.. entries]), Env("Production"));

        act.Should().Throw<InvalidOperationException>();
    }

    // ── Factory boundary: Anonymous mode (Sprint 1) ───────────────────────────

    [Fact]
    public void AddProviderNeutralAuthentication_AnonymousInDevelopment_WiresAnonymousStackAndReturnsNull()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Development may enable Anonymous with just the explicit mode — no AllowHosted, no key.
        EntraAuthOptions? options = services.AddProviderNeutralAuthentication(
            Config(("Authentication:Mode", "Anonymous")), Env("Development"));

        // Anonymous wires its own authorization internally, so it returns null (Program.cs must
        // NOT layer the Entra policy on top).
        options.Should().BeNull();

        ServiceProvider provider = services.BuildServiceProvider();
        provider.GetService<AuthenticationModeOptions>()!.Mode.Should().Be(AuthenticationMode.Anonymous);

        // The anonymous principal normalizer and session token service are registered and usable.
        IPrincipalNormalizer normalizer = provider.GetRequiredService<IPrincipalNormalizer>();
        normalizer.Mode.Should().Be(AuthenticationMode.Anonymous);

        provider.GetService<Api.Security.Anonymous.IAnonymousSessionTokenService>()
            .Should().NotBeNull("Development Anonymous mode generates an ephemeral signing key");
        provider.GetService<Api.Security.Anonymous.AnonymousUsageBudget>()
            .Should().NotBeNull();
    }

    [Fact]
    public void AddProviderNeutralAuthentication_HostedAnonymousWithoutAllowHosted_FailsClosed()
    {
        var services = new ServiceCollection();

        // A hosted (non-Development) Anonymous deployment without the SECOND explicit opt-in
        // must fail startup — no scheme is wired.
        Action act = () => services.AddProviderNeutralAuthentication(
            Config(("Authentication:Mode", "Anonymous")), Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*AllowHosted*");
    }

    [Fact]
    public void AddProviderNeutralAuthentication_HostedAnonymousWithoutSigningKey_FailsClosed()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddProviderNeutralAuthentication(
            Config(
                ("Authentication:Mode", "Anonymous"),
                ("Anonymous:AllowHosted", "true"),
                ("Anonymous:Limits:DailyMaxRequests", "100"),
                ("Anonymous:Limits:DailyMaxTokens", "50000"),
                ("Anonymous:Limits:DailyMaxCostUsd", "2.5")),
            Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*SigningKey*");
    }

    [Fact]
    public void AddProviderNeutralAuthentication_HostedAnonymousFullyConfigured_Wires()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        EntraAuthOptions? options = services.AddProviderNeutralAuthentication(
            Config(
                ("Authentication:Mode", "Anonymous"),
                ("Anonymous:AllowHosted", "true"),
                ("Anonymous:SigningKey", "this-is-a-32-byte-minimum-signing-key!!"),
                ("Anonymous:Limits:DailyMaxRequests", "100"),
                ("Anonymous:Limits:DailyMaxTokens", "50000"),
                ("Anonymous:Limits:DailyMaxCostUsd", "2.5")),
            Env("Production"));

        options.Should().BeNull();

        ServiceProvider provider = services.BuildServiceProvider();
        Api.Security.Anonymous.AnonymousAuthOptions resolved =
            provider.GetRequiredService<Api.Security.Anonymous.AnonymousAuthOptions>();
        resolved.HostedGuardrailsEnforced.Should().BeTrue();
        resolved.HasConfiguredSigningKey.Should().BeTrue();

        Api.Security.Anonymous.AnonymousUsageBudget budget =
            provider.GetRequiredService<Api.Security.Anonymous.AnonymousUsageBudget>();
        budget.Enforced.Should().BeTrue("hosted Anonymous must enforce the daily circuit breaker");
    }

    [Theory]
    [InlineData("Nope")]
    [InlineData("2")]
    public void AddProviderNeutralAuthentication_MalformedMode_FailsStartup(string mode)
    {
        var services = new ServiceCollection();

        Action act = () => services.AddProviderNeutralAuthentication(EntraConfig(mode), Env("Production"));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddProviderNeutralAuthentication_ProductionMissingMode_FailsStartup()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddProviderNeutralAuthentication(EntraConfig(mode: null), Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*not configured*");
    }
}

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
///   <item>GitHub resolves as a known mode but the factory fails startup
///     ("not implemented in this sprint") and never falls through to Entra/dev/anonymous.</item>
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

    // ── Factory boundary: GitHub still fails closed, no fall-through ───────────

    [Theory]
    [InlineData("GitHub")]
    public void AddProviderNeutralAuthentication_UnimplementedMode_FailsStartup(string mode)
    {
        var services = new ServiceCollection();

        Action act = () => services.AddProviderNeutralAuthentication(EntraConfig(mode), Env("Development"));

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*not implemented in this sprint*");
    }

    [Theory]
    [InlineData("GitHub")]
    public void AddProviderNeutralAuthentication_UnimplementedMode_DoesNotWireAnyAuth(string mode)
    {
        var services = new ServiceCollection();

        try
        {
            services.AddProviderNeutralAuthentication(EntraConfig(mode), Env("Production"));
        }
        catch (NotSupportedException)
        {
            // expected — the boundary fails closed
        }

        // Nothing auth-related may be registered: no fall-through to the Entra boundary,
        // no authentication scheme, no options singletons.
        services.Should().NotContain(d => d.ServiceType == typeof(EntraAuthOptions),
            "an unimplemented mode must never register the Entra options");
        services.Should().NotContain(d => d.ServiceType == typeof(IPrincipalNormalizer),
            "an unimplemented mode must never register a principal normalizer");
        services.Should().NotContain(d => d.ServiceType == typeof(AuthenticationModeOptions),
            "an unimplemented mode must not register a resolved mode");
        services.Should().NotContain(
            d => d.ServiceType.FullName == "Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider",
            "an unimplemented mode must never register an authentication scheme");
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

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using RetailPulse.Api.Security.GitHub;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Fail-closed validation contract for <see cref="GitHubAuthOptions.FromConfiguration"/>.
///
/// GitHub mode ultimately mints a full <c>RetailPulse.User</c> session, so a misconfigured deployment
/// must never serve traffic. These prove:
/// <list type="bullet">
///   <item>a complete hosted config resolves and requests MINIMAL scopes (never repo);</item>
///   <item>every missing/placeholder/malformed value throws at startup;</item>
///   <item>the signing key is required and ≥256-bit hosted; ephemeral only in Development;</item>
///   <item>an empty allowlist is rejected (never admit every GitHub user);</item>
///   <item>read:org is requested ONLY when an org allowlist is configured.</item>
/// </list>
/// </summary>
public sealed class GitHubAuthOptionsTests
{
    private const string StrongKey = "github-mode-test-signing-key-0123456789abcdef";

    private static IHostEnvironment Env(string name) => new TestEnv { EnvironmentName = name };

    private sealed class TestEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "RetailPulse.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static IConfiguration Config(IEnumerable<KeyValuePair<string, string?>> entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(entries).Build();

    private static Dictionary<string, string?> Complete() => new()
    {
        ["GitHub:ClientId"] = "Iv1.abcdef0123456789",
        ["GitHub:ClientSecret"] = "test-github-client-secret",
        ["GitHub:SigningKey"] = StrongKey,
        ["GitHub:CallbackUrl"] = "https://api.example.com/api/auth/github/callback",
        ["GitHub:FrontendReturnUrl"] = "https://app.example.com/auth/github/callback",
        ["GitHub:AllowedUserIds:0"] = "12345",
        ["GitHub:RequireSecureCookies"] = "true",
        ["GitHub:AcknowledgeSingleReplica"] = "true",
    };

    [Fact]
    public void FromConfiguration_CompleteHosted_Resolves()
    {
        var options = GitHubAuthOptions.FromConfiguration(Config(Complete()), Env("Production"));

        options.ClientId.Should().Be("Iv1.abcdef0123456789");
        options.HasConfiguredSigningKey.Should().BeTrue();
        options.AllowedUserIds.Should().ContainSingle().Which.Should().Be(12345);
        options.CallbackUrl.Should().EndWith("/api/auth/github/callback");
    }

    [Fact]
    public void RequestedScopes_NoOrgAllowlist_IsEmpty()
    {
        var options = GitHubAuthOptions.FromConfiguration(Config(Complete()), Env("Production"));

        options.RequestedScopes.Should().BeEmpty("reading id + login from /user needs no scope");
    }

    [Fact]
    public void RequestedScopes_WithOrgAllowlist_IsReadOrgOnly_NeverRepo()
    {
        Dictionary<string, string?> cfg = Complete();
        cfg["GitHub:AllowedOrgs:0"] = "contoso";

        var options = GitHubAuthOptions.FromConfiguration(Config(cfg), Env("Production"));

        options.RequestedScopes.Should().Be("read:org");
        options.RequestedScopes.Should().NotContain("repo");
    }

    [Fact]
    public void FromConfiguration_MissingClientId_FailsClosed()
    {
        Dictionary<string, string?> cfg = Complete();
        cfg.Remove("GitHub:ClientId");

        Action act = () => GitHubAuthOptions.FromConfiguration(Config(cfg), Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*ClientId*");
    }

    [Theory]
    [InlineData("GitHub:ClientId", "<github-oauth-app-client-id>")]
    [InlineData("GitHub:ClientSecret", "<set-via-secret-store>")]
    [InlineData("GitHub:SigningKey", "<set-via-secret-store-min-32-bytes>")]
    public void FromConfiguration_PlaceholderValue_IsTreatedAsAbsentAndFailsClosed(string key, string placeholder)
    {
        Dictionary<string, string?> cfg = Complete();
        cfg[key] = placeholder;

        Action act = () => GitHubAuthOptions.FromConfiguration(Config(cfg), Env("Production"));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void FromConfiguration_MissingClientSecret_FailsClosed_EvenInDevelopment()
    {
        Dictionary<string, string?> cfg = Complete();
        cfg.Remove("GitHub:ClientSecret");
        cfg.Remove("GitHub:SigningKey"); // Development allows ephemeral key

        Action act = () => GitHubAuthOptions.FromConfiguration(Config(cfg), Env("Development"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*ClientSecret*");
    }

    [Fact]
    public void FromConfiguration_HostedWithoutSigningKey_FailsClosed()
    {
        Dictionary<string, string?> cfg = Complete();
        cfg.Remove("GitHub:SigningKey");

        Action act = () => GitHubAuthOptions.FromConfiguration(Config(cfg), Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*SigningKey*");
    }

    [Fact]
    public void FromConfiguration_DevelopmentWithoutSigningKey_ResolvesWithEphemeralAllowed()
    {
        Dictionary<string, string?> cfg = Complete();
        cfg.Remove("GitHub:SigningKey");

        var options = GitHubAuthOptions.FromConfiguration(Config(cfg), Env("Development"));

        options.HasConfiguredSigningKey.Should().BeFalse("Development permits an ephemeral process-local key");
    }

    [Fact]
    public void FromConfiguration_ShortSigningKey_FailsClosed()
    {
        Dictionary<string, string?> cfg = Complete();
        cfg["GitHub:SigningKey"] = "too-short";

        Action act = () => GitHubAuthOptions.FromConfiguration(Config(cfg), Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*256-bit*");
    }

    [Fact]
    public void FromConfiguration_EmptyAllowlist_FailsClosed()
    {
        Dictionary<string, string?> cfg = Complete();
        cfg.Remove("GitHub:AllowedUserIds:0");

        Action act = () => GitHubAuthOptions.FromConfiguration(Config(cfg), Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*allowlist*");
    }

    [Theory]
    [InlineData("http://api.example.com/api/auth/github/callback")] // not HTTPS
    [InlineData("https://api.example.com/wrong/path")] // not the callback route
    public void FromConfiguration_BadCallbackUrl_FailsClosed(string callback)
    {
        Dictionary<string, string?> cfg = Complete();
        cfg["GitHub:CallbackUrl"] = callback;

        Action act = () => GitHubAuthOptions.FromConfiguration(Config(cfg), Env("Production"));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void FromConfiguration_NonHttpsFrontendUrl_FailsClosed()
    {
        Dictionary<string, string?> cfg = Complete();
        cfg["GitHub:FrontendReturnUrl"] = "http://app.example.com/auth/github/callback";

        Action act = () => GitHubAuthOptions.FromConfiguration(Config(cfg), Env("Production"));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void FromConfiguration_NonNumericAllowedUserId_FailsClosed()
    {
        Dictionary<string, string?> cfg = Complete();
        cfg["GitHub:AllowedUserIds:0"] = "octocat"; // login, not a numeric id

        Action act = () => GitHubAuthOptions.FromConfiguration(Config(cfg), Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*non-numeric*");
    }

    [Fact]
    public void FromConfiguration_LoginConfig_NeverExposesAccessGrantSurface()
    {
        // A login-only allowlist must fail closed: the mutable handle can never be the sole gate.
        Dictionary<string, string?> cfg = Complete();
        cfg.Remove("GitHub:AllowedUserIds:0");
        cfg["GitHub:AllowedLogins:0"] = "octocat";

        Action act = () => GitHubAuthOptions.FromConfiguration(Config(cfg), Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*allowlist*");
    }

    [Fact]
    public void FromConfiguration_HostedWithoutSecureCookies_FailsClosed()
    {
        Dictionary<string, string?> cfg = Complete();
        cfg["GitHub:RequireSecureCookies"] = "false";

        Action act = () => GitHubAuthOptions.FromConfiguration(Config(cfg), Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*RequireSecureCookies*");
    }

    [Fact]
    public void FromConfiguration_DevelopmentInsecureCookies_Resolves()
    {
        // Development may opt into an insecure, non-__Host dev cookie over plain http://localhost.
        Dictionary<string, string?> cfg = Complete();
        cfg.Remove("GitHub:SigningKey");
        cfg["GitHub:RequireSecureCookies"] = "false";
        cfg["GitHub:AcknowledgeSingleReplica"] = "false";

        var options = GitHubAuthOptions.FromConfiguration(Config(cfg), Env("Development"));

        options.RequireSecureCookies.Should().BeFalse();
    }

    [Fact]
    public void FromConfiguration_HostedWithoutSingleReplicaAck_FailsClosed()
    {
        Dictionary<string, string?> cfg = Complete();
        cfg.Remove("GitHub:AcknowledgeSingleReplica");

        Action act = () => GitHubAuthOptions.FromConfiguration(Config(cfg), Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*AcknowledgeSingleReplica*");
    }

    [Fact]
    public void FromConfiguration_WeakAdditionalValidationKey_FailsClosed()
    {
        Dictionary<string, string?> cfg = Complete();
        cfg["GitHub:AdditionalValidationKeys:0"] = "too-short";

        Action act = () => GitHubAuthOptions.FromConfiguration(Config(cfg), Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*256-bit*");
    }

    [Fact]
    public void FromConfiguration_StrongAdditionalValidationKeys_ResolveDedupedAndPlaceholderStripped()
    {
        Dictionary<string, string?> cfg = Complete();
        cfg["GitHub:AdditionalValidationKeys:0"] = "github-rotated-previous-key-0123456789abcdef";
        cfg["GitHub:AdditionalValidationKeys:1"] = "github-rotated-previous-key-0123456789abcdef"; // dupe
        cfg["GitHub:AdditionalValidationKeys:2"] = "<optional-previous-or-next-signing-key-min-32-bytes>"; // placeholder

        var options = GitHubAuthOptions.FromConfiguration(Config(cfg), Env("Production"));

        options.AdditionalValidationKeys.Should().ContainSingle()
            .Which.Should().Be("github-rotated-previous-key-0123456789abcdef");
    }

    [Fact]
    public void FromConfiguration_AllowedUserIds_ParsedPositiveAndDeduped()
    {
        Dictionary<string, string?> cfg = Complete();
        cfg["GitHub:AllowedUserIds:1"] = "12345"; // dupe
        cfg["GitHub:AllowedUserIds:2"] = "67890";

        var options = GitHubAuthOptions.FromConfiguration(Config(cfg), Env("Production"));

        options.AllowedUserIds.Should().BeEquivalentTo([12345L, 67890L]);
    }

    [Fact]
    public void FromConfiguration_NonPositiveAllowedUserId_FailsClosed()
    {
        Dictionary<string, string?> cfg = Complete();
        cfg["GitHub:AllowedUserIds:0"] = "0";

        Action act = () => GitHubAuthOptions.FromConfiguration(Config(cfg), Env("Production"));

        act.Should().Throw<InvalidOperationException>();
    }
}

using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using RetailPulse.Api.Auth;
using RetailPulse.Api.Security;
using RetailPulse.Api.Security.GitHub;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Unit contract for the GitHub session token service, signing key provider, and principal
/// normalizer.
///
/// Proves the identity is anchored to the IMMUTABLE numeric id (never the mutable login), the token
/// carries the separate GitHub issuer/audience/provider + role + scope + jti and is HS256-signed, and
/// the normalizer trusts only a well-formed <c>github:&lt;digits&gt;</c> subject with a GitHub provider
/// stamp.
/// </summary>
public sealed class GitHubSessionTokenTests
{
    private const string Key = "github-mode-test-signing-key-0123456789abcdef";

    private static GitHubAuthOptions Options() => GitHubAuthOptions.FromConfiguration(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GitHub:ClientId"] = "Iv1.abc",
            ["GitHub:ClientSecret"] = "secret",
            ["GitHub:SigningKey"] = Key,
            ["GitHub:CallbackUrl"] = "https://api.example.com/api/auth/github/callback",
            ["GitHub:FrontendReturnUrl"] = "https://app.example.com/auth/github/callback",
            ["GitHub:AllowedUserIds:0"] = "12345",
            ["GitHub:AcknowledgeSingleReplica"] = "true",
        }).Build(),
        new TestEnv());

    private static GitHubAuthOptions OptionsWithRotation(string signingKey, params string[] additional)
    {
        var cfg = new Dictionary<string, string?>
        {
            ["GitHub:ClientId"] = "Iv1.abc",
            ["GitHub:ClientSecret"] = "secret",
            ["GitHub:SigningKey"] = signingKey,
            ["GitHub:CallbackUrl"] = "https://api.example.com/api/auth/github/callback",
            ["GitHub:FrontendReturnUrl"] = "https://app.example.com/auth/github/callback",
            ["GitHub:AllowedUserIds:0"] = "12345",
            ["GitHub:AcknowledgeSingleReplica"] = "true",
        };
        for (int i = 0; i < additional.Length; i++)
        {
            cfg[$"GitHub:AdditionalValidationKeys:{i}"] = additional[i];
        }

        return GitHubAuthOptions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(cfg).Build(), new TestEnv());
    }

    private sealed class TestEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "RetailPulse.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static (string Token, GitHubAuthOptions Opts, GitHubSigningKeyProvider KeyProvider) Mint(
        long id = 12345, string login = "octocat")
    {
        GitHubAuthOptions opts = Options();
        var keyProvider = new GitHubSigningKeyProvider(opts);
        var svc = new GitHubSessionTokenService(opts, keyProvider);
        GitHubSession session = svc.CreateSession(new GitHubVerifiedUser(id, login));
        return (session.Token, opts, keyProvider);
    }

    private static ClaimsPrincipal Validate(string token, GitHubAuthOptions opts, GitHubSigningKeyProvider keyProvider)
    {
        var handler = new JsonWebTokenHandler { MapInboundClaims = false };
        TokenValidationResult result = handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuers = [opts.Issuer],
            ValidAudiences = [opts.Audience],
            IssuerSigningKeys = keyProvider.ValidationKeys,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            RoleClaimType = "roles",
            NameClaimType = JwtRegisteredClaimNames.Sub,
        }).GetAwaiter().GetResult();

        result.IsValid.Should().BeTrue();
        return new ClaimsPrincipal(result.ClaimsIdentity);
    }

    [Fact]
    public void CreateSession_SubjectIsImmutableNumericId()
    {
        (string token, GitHubAuthOptions opts, GitHubSigningKeyProvider kp) = Mint(id: 99001, login: "renamed-login");

        ClaimsPrincipal principal = Validate(token, opts, kp);

        principal.FindFirst(JwtRegisteredClaimNames.Sub)!.Value.Should().Be("github:99001");
        // The login is present but informational only.
        principal.FindFirst(GitHubAuthConstants.LoginClaimType)!.Value.Should().Be("renamed-login");
    }

    [Fact]
    public void CreateSession_CarriesProviderRoleScopeAndJti()
    {
        (string token, GitHubAuthOptions opts, GitHubSigningKeyProvider kp) = Mint();

        ClaimsPrincipal principal = Validate(token, opts, kp);

        principal.FindFirst("provider")!.Value.Should().Be("GitHub");
        principal.FindFirst("roles")!.Value.Should().Be("RetailPulse.User");
        principal.FindFirst("scp")!.Value.Should().Be("access_as_user");
        principal.FindFirst(JwtRegisteredClaimNames.Jti)!.Value.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CreateSession_IsHs256Signed()
    {
        (string token, _, _) = Mint();

        var jwt = new JsonWebToken(token);
        jwt.Alg.Should().Be(SecurityAlgorithms.HmacSha256);
    }

    [Fact]
    public void CreateSession_RejectsNonPositiveId()
    {
        GitHubAuthOptions opts = Options();
        var svc = new GitHubSessionTokenService(opts, new GitHubSigningKeyProvider(opts));

        Action act = () => svc.CreateSession(new GitHubVerifiedUser(0, "x"));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SigningKeyProvider_Configured_IsNotEphemeralAndStrong()
    {
        var kp = new GitHubSigningKeyProvider(Options());

        kp.IsEphemeral.Should().BeFalse();
        kp.Key.KeySize.Should().BeGreaterThanOrEqualTo(256);
    }

    // ── Signing-key rotation ────────────────────────────────────────────────────

    private const string OldKey = "github-OLD-signing-key-0123456789abcdefZZZZ";
    private const string NewKey = "github-NEW-signing-key-0123456789abcdefYYYY";

    [Fact]
    public void Rotation_TokenSignedWithPreviousKey_StillValidatesAfterRotation()
    {
        // Before rotation: the OLD key is the sole signing key; mint a session token with it.
        GitHubAuthOptions before = OptionsWithRotation(OldKey);
        var beforeKp = new GitHubSigningKeyProvider(before);
        GitHubSession oldSession = new GitHubSessionTokenService(before, beforeKp)
            .CreateSession(new GitHubVerifiedUser(12345, "octocat"));

        // After rotation: NEW key signs, OLD key demoted to validation-only. The in-flight OLD token
        // must keep validating against the rotated key set.
        GitHubAuthOptions after = OptionsWithRotation(NewKey, OldKey);
        var afterKp = new GitHubSigningKeyProvider(after);

        ClaimsPrincipal principal = Validate(oldSession.Token, after, afterKp);

        principal.FindFirst(JwtRegisteredClaimNames.Sub)!.Value.Should().Be("github:12345");
    }

    [Fact]
    public void Rotation_NewTokensAreSignedWithCurrentKey_NotThePreviousKey()
    {
        GitHubAuthOptions after = OptionsWithRotation(NewKey, OldKey);
        var afterKp = new GitHubSigningKeyProvider(after);
        GitHubSession newSession = new GitHubSessionTokenService(after, afterKp)
            .CreateSession(new GitHubVerifiedUser(12345, "octocat"));

        // Freshly-minted tokens must be signed by the CURRENT key (listed first), never a rotation key.
        var jwt = new JsonWebToken(newSession.Token);
        jwt.Kid.Should().Be(afterKp.Key.KeyId);
        jwt.Kid.Should().NotBe(new GitHubSigningKeyProvider(OptionsWithRotation(OldKey)).Key.KeyId,
            "the new token's kid identifies the current key, not the demoted one");

        // And a validator that ONLY trusts the OLD key must reject the new token.
        GitHubAuthOptions oldOnly = OptionsWithRotation(OldKey);
        var handler = new JsonWebTokenHandler { MapInboundClaims = false };
        TokenValidationResult result = handler.ValidateTokenAsync(newSession.Token, new TokenValidationParameters
        {
            ValidIssuers = [after.Issuer],
            ValidAudiences = [after.Audience],
            IssuerSigningKeys = new GitHubSigningKeyProvider(oldOnly).ValidationKeys,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        }).GetAwaiter().GetResult();

        result.IsValid.Should().BeFalse("the new token is signed by the current key, not the old one");
    }

    // ── Normalizer ────────────────────────────────────────────────────────────

    private static ClaimsPrincipal PrincipalWith(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));

    [Fact]
    public void Normalizer_TrustsImmutableNumericSubject()
    {
        var normalizer = new GitHubPrincipalNormalizer();
        ClaimsPrincipal principal = PrincipalWith(
            new Claim(JwtRegisteredClaimNames.Sub, "github:12345"),
            new Claim("provider", "GitHub"),
            new Claim(GitHubAuthConstants.LoginClaimType, "octocat"),
            new Claim("roles", "RetailPulse.User"),
            new Claim("scp", "access_as_user"));

        NormalizedPrincipal normalized = normalizer.Normalize(principal);

        normalized.Provider.Should().Be("GitHub");
        normalized.Subject.Should().Be("github:12345");
        normalized.DisplayName.Should().Be("octocat", "login is display only");
        normalized.Roles.Should().Contain("RetailPulse.User");
        normalized.Scopes.Should().Contain("access_as_user");
    }

    [Theory]
    [InlineData("octocat")] // mutable login as subject
    [InlineData("github:")] // no digits
    [InlineData("github:abc")] // non-numeric
    [InlineData("github:-1")] // non-positive
    [InlineData("gitlab:12345")] // wrong prefix
    public void Normalizer_RejectsNonNumericOrMalformedSubject(string subject)
    {
        var normalizer = new GitHubPrincipalNormalizer();
        ClaimsPrincipal principal = PrincipalWith(
            new Claim(JwtRegisteredClaimNames.Sub, subject),
            new Claim("provider", "GitHub"));

        Action act = () => normalizer.Normalize(principal);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Normalizer_RejectsNonGitHubProvider()
    {
        var normalizer = new GitHubPrincipalNormalizer();
        ClaimsPrincipal principal = PrincipalWith(
            new Claim(JwtRegisteredClaimNames.Sub, "github:12345"),
            new Claim("provider", "Entra"));

        Action act = () => normalizer.Normalize(principal);

        act.Should().Throw<InvalidOperationException>().WithMessage("*provider*");
    }

    [Fact]
    public void Normalizer_ModeIsGitHub() =>
        new GitHubPrincipalNormalizer().Mode.Should().Be(AuthenticationMode.GitHub);
}

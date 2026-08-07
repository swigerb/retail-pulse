using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace RetailPulse.Api.Security.GitHub;

/// <summary>A verified GitHub identity, established server-side AFTER the confidential token
/// exchange, the <c>/user</c> validation call, and the server-side allowlist check succeed.</summary>
/// <param name="UserId">The immutable numeric GitHub user id — the SOLE basis for identity.</param>
/// <param name="Login">The mutable login handle — informational only, never identity.</param>
public readonly record struct GitHubVerifiedUser(long UserId, string Login);

/// <summary>Result of minting a GitHub session credential.</summary>
/// <param name="Token">The signed, short-lived bearer/access token.</param>
/// <param name="Subject">The immutable subject (<c>github:&lt;id&gt;</c>).</param>
/// <param name="ExpiresInSeconds">Seconds until the token expires.</param>
public readonly record struct GitHubSession(string Token, string Subject, int ExpiresInSeconds);

/// <summary>
/// Issues short-lived, server-signed Retail Pulse GitHub session tokens from an already-verified
/// GitHub identity.
///
/// The token is a compact HS256 JWT whose issuer/audience/provider are SEPARATE from Entra and
/// Anonymous, so a GitHub token can never satisfy another provider's policy and vice versa. Identity
/// is the immutable numeric id (<c>sub = github:&lt;id&gt;</c>); the login is carried only as an
/// informational claim. There is no refresh token — the client re-runs the OAuth flow when the short
/// TTL elapses, which bounds replay. The GitHub PROVIDER token is never placed in this token or
/// stored anywhere; it is used only transiently to call <c>/user</c> during verification.
/// </summary>
public interface IGitHubSessionTokenService
{
    /// <summary>Mints a signed session token for a verified GitHub user.</summary>
    GitHubSession CreateSession(GitHubVerifiedUser user);
}

/// <inheritdoc />
public sealed class GitHubSessionTokenService : IGitHubSessionTokenService
{
    private readonly GitHubAuthOptions _options;
    private readonly GitHubSigningKeyProvider _keyProvider;
    private readonly TimeProvider _timeProvider;
    private readonly JsonWebTokenHandler _handler = new();

    public GitHubSessionTokenService(
        GitHubAuthOptions options,
        GitHubSigningKeyProvider keyProvider,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public GitHubSession CreateSession(GitHubVerifiedUser user)
    {
        if (user.UserId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(user), "A GitHub session requires a positive immutable numeric user id.");
        }

        string subject = GitHubAuthConstants.SubjectPrefix + user.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        DateTime expires = now.AddSeconds(_options.SessionTokenTtlSeconds);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(GitHubAuthConstants.ProviderClaimType, GitHubAuthConstants.ProviderName),
            new("roles", _options.Role),
            new("scp", _options.Scope),
        };

        // Login is informational only — recorded for display/audit, never used for authorization.
        if (!string.IsNullOrWhiteSpace(user.Login))
        {
            claims.Add(new Claim(GitHubAuthConstants.LoginClaimType, user.Login));
            claims.Add(new Claim("name", user.Login));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = new SigningCredentials(_keyProvider.Key, SecurityAlgorithms.HmacSha256),
        };

        string token = _handler.CreateToken(descriptor);
        return new GitHubSession(token, subject, _options.SessionTokenTtlSeconds);
    }
}

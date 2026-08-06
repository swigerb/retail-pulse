using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace RetailPulse.Api.Security.Anonymous;

/// <summary>Result of minting an anonymous session credential.</summary>
/// <param name="Token">The signed, short-lived bearer/access token.</param>
/// <param name="Subject">The cryptographically random, immutable per-session subject.</param>
/// <param name="ExpiresInSeconds">Seconds until the token expires.</param>
public readonly record struct AnonymousSession(string Token, string Subject, int ExpiresInSeconds);

/// <summary>
/// Issues short-lived, server-signed anonymous session tokens.
///
/// Identity is minted by the SERVER, never taken from a client header: each call produces a fresh,
/// cryptographically random subject. The token is a compact HS256 JWT carrying only non-PII claims
/// (subject, provider, constrained role/scope, issuer/audience, strict expiry, random jti). There
/// is no refresh token — the client re-bootstraps when the short TTL elapses, which bounds replay.
/// </summary>
public interface IAnonymousSessionTokenService
{
    /// <summary>Creates a new anonymous session with a fresh random subject and a signed token.</summary>
    AnonymousSession CreateSession();
}

/// <inheritdoc />
public sealed class AnonymousSessionTokenService : IAnonymousSessionTokenService
{
    private readonly AnonymousAuthOptions _options;
    private readonly AnonymousSigningKeyProvider _keyProvider;
    private readonly TimeProvider _timeProvider;
    private readonly JsonWebTokenHandler _handler = new();

    public AnonymousSessionTokenService(
        AnonymousAuthOptions options,
        AnonymousSigningKeyProvider keyProvider,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public AnonymousSession CreateSession()
    {
        string subject = NewSubject();
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        DateTime expires = now.AddSeconds(_options.SessionTokenTtlSeconds);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, subject),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
                new Claim(AnonymousCapabilityPolicy.ProviderClaimType, AnonymousCapabilityPolicy.ProviderName),
                new Claim("roles", _options.Role),
                new Claim("scp", _options.Scope),
            ]),
            SigningCredentials = new SigningCredentials(
                _keyProvider.Key,
                SecurityAlgorithms.HmacSha256),
        };

        string token = _handler.CreateToken(descriptor);
        return new AnonymousSession(token, subject, _options.SessionTokenTtlSeconds);
    }

    /// <summary>16 random bytes, base64url-encoded, no PII. Immutable for the session lifetime.</summary>
    private static string NewSubject()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return "anon-" + Base64UrlEncoder.Encode(bytes.ToArray());
    }
}

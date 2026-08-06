using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using RetailPulse.Api.Security;
using RetailPulse.Api.Security.Anonymous;

namespace RetailPulse.Api.Auth;

/// <summary>
/// Maps a validated anonymous session <see cref="ClaimsPrincipal"/> onto the provider-neutral
/// <see cref="NormalizedPrincipal"/>.
///
/// The subject comes exclusively from the server-signed token's <c>sub</c> claim — the immutable,
/// cryptographically random per-session id minted by <see cref="AnonymousSessionTokenService"/>.
/// A caller-supplied user id (request body or header) is NEVER used as identity, so anonymous
/// sessions cannot spoof or collide with one another. Roles and scopes are pinned to the
/// constrained anonymous set and provider is always <c>Anonymous</c>.
/// </summary>
public sealed class AnonymousPrincipalNormalizer : IPrincipalNormalizer
{
    public AuthenticationMode Mode => AuthenticationMode.Anonymous;

    public NormalizedPrincipal Normalize(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        string? subject =
            principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new InvalidOperationException(
                "Cannot normalize an anonymous principal without a server-issued subject claim.");
        }

        // Defense in depth: the token must carry the anonymous provider stamp. A token from any
        // other provider must never normalize as Anonymous.
        string? provider = principal.FindFirst(AnonymousCapabilityPolicy.ProviderClaimType)?.Value;
        if (!string.Equals(provider, AnonymousCapabilityPolicy.ProviderName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Cannot normalize a principal whose provider claim is not 'Anonymous'.");
        }

        string[] roles = [.. principal.FindAll("roles").Select(c => c.Value)
            .Concat(principal.FindAll(ClaimTypes.Role).Select(c => c.Value))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)];

        string[] scopes = [.. principal.FindAll("scp").Select(c => c.Value)
            .SelectMany(v => v.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)];

        return new NormalizedPrincipal(
            Provider: AnonymousCapabilityPolicy.ProviderName,
            Subject: subject,
            DisplayName: null,
            Roles: roles,
            Scopes: scopes);
    }
}

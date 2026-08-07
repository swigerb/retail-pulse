using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using RetailPulse.Api.Security;
using RetailPulse.Api.Security.GitHub;

namespace RetailPulse.Api.Auth;

/// <summary>
/// Maps a validated GitHub session <see cref="ClaimsPrincipal"/> onto the provider-neutral
/// <see cref="NormalizedPrincipal"/>.
///
/// The subject comes exclusively from the server-signed token's <c>sub</c> claim — the immutable
/// <c>github:&lt;numeric id&gt;</c> established at login. The mutable login handle is projected only as
/// the <see cref="NormalizedPrincipal.DisplayName"/> (informational) and is NEVER used as identity, so
/// a renamed GitHub account cannot impersonate another. Roles and scopes are pinned to the verified
/// GitHub set and the provider is always <c>GitHub</c>.
/// </summary>
public sealed class GitHubPrincipalNormalizer : IPrincipalNormalizer
{
    public AuthenticationMode Mode => AuthenticationMode.GitHub;

    public NormalizedPrincipal Normalize(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        string? subject =
            principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        // Trust ONLY an immutable numeric subject of the exact shape github:<digits>. A missing or
        // malformed subject — or a subject that is really the mutable login — must never normalize.
        if (string.IsNullOrWhiteSpace(subject)
            || !subject.StartsWith(GitHubAuthConstants.SubjectPrefix, StringComparison.Ordinal)
            || !long.TryParse(subject.AsSpan(GitHubAuthConstants.SubjectPrefix.Length), out long id)
            || id <= 0)
        {
            throw new InvalidOperationException(
                "Cannot normalize a GitHub principal without an immutable numeric 'github:<id>' subject.");
        }

        // Defense in depth: the token must carry the GitHub provider stamp.
        string? provider = principal.FindFirst(GitHubAuthConstants.ProviderClaimType)?.Value;
        if (!string.Equals(provider, GitHubAuthConstants.ProviderName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Cannot normalize a principal whose provider claim is not 'GitHub'.");
        }

        // Informational display only — never identity.
        string? displayName =
            principal.FindFirst(GitHubAuthConstants.LoginClaimType)?.Value
            ?? principal.FindFirst("name")?.Value;

        string[] roles = [.. principal.FindAll("roles").Select(c => c.Value)
            .Concat(principal.FindAll(ClaimTypes.Role).Select(c => c.Value))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)];

        string[] scopes = [.. principal.FindAll("scp").Select(c => c.Value)
            .SelectMany(v => v.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)];

        return new NormalizedPrincipal(
            Provider: GitHubAuthConstants.ProviderName,
            Subject: subject,
            DisplayName: displayName,
            Roles: roles,
            Scopes: scopes);
    }
}

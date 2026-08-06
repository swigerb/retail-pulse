using System.Security.Claims;
using RetailPulse.Api.Security;

namespace RetailPulse.Api.Auth;

/// <summary>
/// Maps a Microsoft Entra <see cref="ClaimsPrincipal"/> onto the provider-neutral
/// <see cref="NormalizedPrincipal"/>.
///
/// The mapping mirrors the claim shapes the existing Entra boundary already validates:
/// <list type="bullet">
///   <item><b>Subject</b> — the immutable <c>oid</c> object identifier via
///     <see cref="UserIdentity.Resolve"/> (short or MS-schema form), so identity is anchored to a
///     stable id and can never be spoofed from a mutable value.</item>
///   <item><b>Roles</b> — the Entra <c>roles</c> claim (plus <see cref="ClaimTypes.Role"/> for the
///     synthetic Development handler).</item>
///   <item><b>Scopes</b> — the space-delimited <c>scp</c> claim (and the long MS-schema form).</item>
///   <item><b>DisplayName</b> — the <c>name</c> claim when present.</item>
/// </list>
/// This is a read-only projection. It does not grant access and does not replace the
/// authorization policy that requires the app role and API scope.
/// </summary>
public sealed class EntraPrincipalNormalizer : IPrincipalNormalizer
{
    private const string _scopeSchemaClaim = "http://schemas.microsoft.com/identity/claims/scope";

    public EntraPrincipalNormalizer(EntraAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
    }

    public AuthenticationMode Mode => AuthenticationMode.Entra;

    public NormalizedPrincipal Normalize(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        string subject = UserIdentity.Resolve(principal);
        if (subject == UserIdentity.AnonymousUserId)
        {
            throw new InvalidOperationException(
                "Cannot normalize an Entra principal without an immutable object identifier claim.");
        }

        string? displayName =
            principal.FindFirst("name")?.Value
            ?? principal.FindFirst(ClaimTypes.Name)?.Value;

        string[] roles = [.. principal.FindAll("roles").Select(c => c.Value)
            .Concat(principal.FindAll(ClaimTypes.Role).Select(c => c.Value))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)];

        string[] scopes = [.. principal.FindAll("scp").Select(c => c.Value)
            .Concat(principal.FindAll(_scopeSchemaClaim).Select(c => c.Value))
            .SelectMany(v => v.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)];

        return new NormalizedPrincipal(
            Provider: Mode.ToString(),
            Subject: subject,
            DisplayName: displayName,
            Roles: roles,
            Scopes: scopes);
    }
}

namespace RetailPulse.Api.Auth;

/// <summary>
/// Provider-neutral view of an authenticated caller. This is the normalized identity/claims
/// model every authentication provider maps onto, so downstream code can reason about identity
/// without depending on Entra-specific claim shapes.
///
/// Introduced by the Sprint 0 authentication foundation as an additive seam. It does NOT change
/// or weaken the live authorization requirements — the API still enforces the
/// <c>RetailPulse.User</c> app role and the <c>access_as_user</c> scope through the existing
/// authorization policy. This value object simply gives later providers (GitHub, Anonymous) a
/// stable target to populate.
/// </summary>
/// <param name="Provider">The authentication provider that issued the identity (e.g. "Entra").</param>
/// <param name="Subject">
/// Stable, immutable identifier for the caller within the provider. For Entra this is the
/// <c>oid</c> object identifier (never the mutable email/UPN), matching
/// <see cref="UserIdentity.Resolve"/>.
/// </param>
/// <param name="DisplayName">Human-readable display name, when the provider supplies one.</param>
/// <param name="Roles">Authorization roles asserted by the provider (Entra <c>roles</c> claim).</param>
/// <param name="Scopes">Delegated scopes asserted by the provider (Entra <c>scp</c> claim).</param>
public sealed record NormalizedPrincipal(
    string Provider,
    string Subject,
    string? DisplayName,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Scopes);

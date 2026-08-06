using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using RetailPulse.Api.Security.Anonymous;

namespace RetailPulse.Api.Auth;

/// <summary>
/// Centralised, provider-aware user-identifier resolution so write paths (chat → agents)
/// and read paths (memory endpoint, etc.) converge on the same value.
///
/// History (Costco, 2026-06-04): the Memory Panel showed "0 memories" even
/// when the Memory Management agent had clearly stored rows. Root cause: the
/// chat endpoint resolved userId from <c>request.User?.ObjectId ?? "anonymous"</c>
/// (always "anonymous" when the FE omits the User block), while the
/// /api/memory endpoint resolved it from <c>HttpContext.User.FindFirst("oid")</c>
/// — which the dev-mode auth handler stamps with the zero GUID. Writes landed
/// under "anonymous"; reads queried under "00000000-…". Use this helper from
/// every endpoint that needs a userId so the two sides cannot drift again.
///
/// Sprint 1 hardening (Costco, 2026-08-06): identity is now resolved per authentication
/// provider and a request-body objectId is NEVER honoured for an authenticated principal
/// (it was previously accepted as a fallback whenever no <c>oid</c> claim was present, which
/// let an authenticated Anonymous token — which carries <c>sub</c> but no <c>oid</c> — be
/// keyed to a spoofable body value or collapse to a shared "anonymous" bucket). Resolution:
/// <list type="bullet">
///   <item><b>Anonymous</b> provider → the immutable, server-minted session <c>sub</c>.</item>
///   <item><b>Entra</b> (and any other authenticated principal) → the immutable <c>oid</c>.</item>
///   <item>An authenticated principal with neither never falls back to the request body.</item>
///   <item>Only a truly unauthenticated caller may supply a body objectId as a last resort.</item>
/// </list>
/// </summary>
public static class UserIdentity
{
    public const string AnonymousUserId = "anonymous";

    /// <summary>
    /// Resolves the canonical userId for memory/audit purposes in a provider-aware, spoof-proof
    /// way. Priority: Anonymous session <c>sub</c> → authenticated <c>oid</c> (short or MS schema
    /// form) → (only when the caller is NOT authenticated) an explicit body value → "anonymous".
    /// A request-body objectId is never trusted for an authenticated principal.
    /// </summary>
    public static string Resolve(ClaimsPrincipal? principal, string? bodyObjectId = null)
    {
        // Anonymous provider: identity is the immutable, cryptographically random session subject
        // minted server-side. A request-body objectId is never honoured for it.
        if (AnonymousCapabilityPolicy.IsAnonymousPrincipal(principal))
        {
            string? sub = principal!.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return !string.IsNullOrWhiteSpace(sub) ? sub : AnonymousUserId;
        }

        // Entra / default: the immutable object identifier (short or MS-schema form).
        string? oid = principal?.FindFirst("oid")?.Value
            ?? principal?.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
        if (!string.IsNullOrWhiteSpace(oid))
        {
            return oid;
        }

        // No provider subject and no oid. An authenticated principal must NEVER be keyed to a
        // spoofable request-body value — fail closed to "anonymous" instead.
        if (principal?.Identity is { IsAuthenticated: true })
        {
            return AnonymousUserId;
        }

        // Unauthenticated caller (e.g. no auth configured): body value permitted as a last resort.
        return !string.IsNullOrWhiteSpace(bodyObjectId) ? bodyObjectId : AnonymousUserId;
    }
}

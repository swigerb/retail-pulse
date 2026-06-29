using System.Security.Claims;

namespace RetailPulse.Api.Auth;

/// <summary>
/// Centralised user-identifier resolution so write paths (chat → agents)
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
/// </summary>
public static class UserIdentity
{
    public const string AnonymousUserId = "anonymous";

    /// <summary>
    /// Resolves the canonical userId for memory/audit purposes.
    /// Priority: authenticated "oid" claim (either short or MS schema form) →
    /// explicit body value (fallback only when no claim) → "anonymous".
    /// Claims are trusted first to prevent request-body spoofing attacks.
    /// </summary>
    public static string Resolve(ClaimsPrincipal? principal, string? bodyObjectId = null)
    {
        string? oid = principal?.FindFirst("oid")?.Value
            ?? principal?.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;

        if (!string.IsNullOrWhiteSpace(oid))
            return oid;

        if (!string.IsNullOrWhiteSpace(bodyObjectId))
            return bodyObjectId;

        return AnonymousUserId;
    }
}

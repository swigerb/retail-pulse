using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace RetailPulse.Api.Security.GitHub;

/// <summary>
/// Derives the per-state name of the browser-bound OAuth state cookie and validates the format of an
/// OAuth <c>state</c> value.
///
/// Each login gets its OWN cookie name so parallel tabs never clash: two concurrent
/// <c>start</c> requests write two differently-named cookies and each callback consumes/deletes ONLY
/// its own. The name suffix is a bounded, URL-safe value DERIVED from the state (a truncated SHA-256,
/// never the raw state bytes), so:
/// <list type="bullet">
///   <item>the callback can recompute the exact cookie name from the (validated-format) state alone;</item>
///   <item>the suffix is always a small, fixed-length token of the cookie-name-safe alphabet
///     <c>[A-Za-z0-9_-]</c>, so a crafted state can never inject cookie attributes or an oversized name;</item>
///   <item>in secure mode the name keeps the <c>__Host-</c> prefix (Secure + Path=/ + no Domain).</item>
/// </list>
/// The state itself must be exactly the shape <see cref="GitHubRandom.NewToken"/> emits — a 43-char
/// base64url token — which is validated BEFORE the name is derived, bounding the work and rejecting
/// any malformed/oversized callback input up front.
/// </summary>
public static class GitHubStateCookie
{
    // Base64url of 32 random bytes is exactly 43 chars (no padding), alphabet [A-Za-z0-9_-].
    private const int _stateLength = 43;

    // Length of the bounded URL-safe suffix appended to the cookie base.
    private const int _suffixLength = 22;

    /// <summary>
    /// True only when <paramref name="state"/> is exactly the fixed-length base64url shape that
    /// <see cref="GitHubRandom.NewToken"/> produces. Rejects null/empty, wrong length, and any
    /// character outside the URL-safe alphabet — so a hostile callback cannot smuggle a huge or
    /// specially-crafted state into cookie-name derivation.
    /// </summary>
    public static bool IsValidStateFormat(string? state)
    {
        if (string.IsNullOrEmpty(state) || state.Length != _stateLength)
        {
            return false;
        }

        foreach (char c in state)
        {
            bool ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The exact cookie name for <paramref name="state"/>. In <paramref name="secure"/> mode the name
    /// keeps the <c>__Host-</c> prefix; otherwise it uses the Development-only insecure base. The state
    /// MUST already be validated with <see cref="IsValidStateFormat"/>.
    /// </summary>
    public static string NameFor(string state, bool secure)
    {
        string @base = secure ? GitHubAuthConstants.SecureStateCookieBase : GitHubAuthConstants.DevStateCookieBase;
        return @base + Suffix(state);
    }

    private static string Suffix(string state)
    {
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(state));
        // Base64url is already the cookie-name-safe alphabet [A-Za-z0-9_-]; truncate to a bounded len.
        return Base64UrlEncoder.Encode(hash)[.._suffixLength];
    }
}

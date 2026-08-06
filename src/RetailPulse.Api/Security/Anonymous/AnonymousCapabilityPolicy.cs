using System.Security.Claims;

namespace RetailPulse.Api.Security.Anonymous;

/// <summary>
/// Central, single-source definition of what an Anonymous-mode principal may and may not do.
///
/// This is deliberately data — a named policy, not fragile per-endpoint or UI-level hiding — so
/// the read-only guard, the chat tool filter, and the tests all reason about the same lists. If a
/// new mutation endpoint or write-capable tool is added, it is denied to anonymous callers by
/// DEFAULT (deny-by-default on non-GET verbs), and only an explicit addition to
/// <see cref="AnonymousReadableNonGetRoutes"/> can widen the anonymous surface.
/// </summary>
public static class AnonymousCapabilityPolicy
{
    /// <summary>The normalized provider name stamped on every anonymous principal.</summary>
    public const string ProviderName = "Anonymous";

    /// <summary>Claim type that records the issuing provider on the session token.</summary>
    public const string ProviderClaimType = "provider";

    /// <summary>Constrained app role every anonymous session carries (never RetailPulse.User).</summary>
    public const string DefaultRole = "RetailPulse.Anonymous";

    /// <summary>Constrained delegated scope every anonymous session carries.</summary>
    public const string DefaultScope = "chat_limited";

    /// <summary>Route of the single unauthenticated anonymous bootstrap endpoint.</summary>
    public const string BootstrapRoute = "/api/auth/anonymous/session";

    /// <summary>
    /// Tool method names that mutate state (create approvals/actions, write memory, etc.). These
    /// are stripped from the tool set exposed to an anonymous principal so the model can never
    /// invoke a write path. Matched against <c>AIFunction.Name</c> (the CLR method name).
    /// </summary>
    public static readonly IReadOnlySet<string> WriteCapableToolNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Creates an approval-gate request and awaits a human action — a write.
            "RequestApproval",
        };

    /// <summary>
    /// The ONLY non-GET routes an anonymous principal may reach. Everything else that is not a
    /// GET/HEAD/OPTIONS is a mutation and is denied. These are read-only query/chat endpoints that
    /// happen to use POST for a request body. The bootstrap route is unauthenticated (handled by
    /// AllowAnonymous) and is included for completeness/robustness.
    /// </summary>
    public static readonly IReadOnlySet<string> AnonymousReadableNonGetRoutes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            BootstrapRoute,
            "/api/chat",
            "/api/chat/stream",
            "/api/council/convene",
            "/api/knowledge/search",
            "/api/message-extension/query",
            "/api/scorecard",
            "/api/escalate",
        };

    /// <summary>
    /// True when the principal is an authenticated Anonymous-provider session (used by the guard
    /// and tool filter to decide whether the read-only / write-tool restrictions apply). Identity
    /// is taken from the validated token's <c>provider</c> claim — never a client header.
    /// </summary>
    public static bool IsAnonymousPrincipal(ClaimsPrincipal? principal) =>
        principal?.Identity is { IsAuthenticated: true }
        && string.Equals(
            principal.FindFirst(ProviderClaimType)?.Value,
            ProviderName,
            StringComparison.Ordinal);

    /// <summary>
    /// True when a request with the given HTTP method and path is a mutation that must be denied
    /// to anonymous callers. GET/HEAD/OPTIONS are always read-safe; any other verb is a mutation
    /// unless the exact path is on the read-only allowlist.
    /// </summary>
    public static bool IsBlockedMutation(string method, string path)
    {
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        {
            return false;
        }

        string normalized = path.Length > 1 ? path.TrimEnd('/') : path;
        return !AnonymousReadableNonGetRoutes.Contains(normalized);
    }
}

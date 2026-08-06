using System.Security.Claims;

namespace RetailPulse.Api.Security.Anonymous;

/// <summary>
/// Central, single-source definition of what an Anonymous-mode principal may and may not do.
///
/// This is deliberately data — a named policy, not fragile per-endpoint or UI-level hiding — so
/// the deny-by-default guard, the chat tool filter, and the tests all reason about the same
/// allowlist. There is NO verb shortcut: a request is denied unless its exact (method, route) is
/// on the closed <see cref="_allowedRoutes"/> allowlist. The entire anonymous surface is exactly
/// TWO routes: the unauthenticated bootstrap <c>POST /api/auth/anonymous/session</c> and the
/// authenticated <c>POST /api/chat</c>.
///
/// Sprint 1 mandate: the SignalR hubs (<c>/hubs/telemetry</c>, <c>/hubs/streaming</c>) are NOT part
/// of the anonymous surface. An anonymous session gets no real-time telemetry or token streaming —
/// a valid anonymous token is denied (403) on both hub negotiate and connection endpoints. Removing
/// the hubs eliminates the Clients.All / global-broadcast and hub group-namespace collision exposure
/// entirely, without refactoring the shared (Entra) telemetry pipeline. The Sprint 3 anonymous
/// frontend simply does not start the hubs.
///
/// Every other mapped endpoint — observability, admin, export, memory, cards, approvals, guardrail
/// logs, all broad GET reads, and every hub route — is denied (403). Read-only charts/data are
/// reached through the filtered chat tool path, never a direct operator endpoint. Widening the
/// surface requires an explicit addition here, reviewed against the runtime inventory tests.
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

    /// <summary>The single authenticated REST capability of an anonymous session.</summary>
    public const string ChatRoute = "/api/chat";

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
            // Persists cross-prompt memory — disabled for Anonymous to eliminate stored injection.
            "RememberPreference",
            "SaveMemory",
        };

    /// <summary>
    /// The EXACT (method, route) pairs an authenticated anonymous principal may reach. This is a
    /// closed allowlist — there is NO GET verb shortcut and NO hub route. Any request whose
    /// (method, path) is not matched here is denied by default. The anonymous surface is exactly two
    /// routes: the authenticated chat POST plus the bootstrap (which is unauthenticated via
    /// AllowAnonymous and therefore never reaches the guard, but is listed for completeness).
    /// Read-only charts/data are served through the filtered chat tool path, never through direct
    /// operator/observability endpoints; real-time telemetry/streaming hubs are not exposed.
    /// </summary>
    private static readonly IReadOnlySet<(string Method, string Route)> _allowedRoutes =
        new HashSet<(string, string)>
        {
            ("POST", BootstrapRoute),
            ("POST", ChatRoute),
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
    /// Deny-by-default authorization for an anonymous principal: returns true ONLY when the exact
    /// (method, path) is on the closed allowlist. Everything else — including every hub route and
    /// every GET to observability/admin/export/memory/cards/approvals/guardrail-log endpoints — is
    /// denied (the guard returns 403).
    /// </summary>
    public static bool IsAllowed(string method, string path)
    {
        // CORS preflight carries no credentials/body and mutates nothing.
        return HttpMethods.IsOptions(method)
            || _allowedRoutes.Contains((method.ToUpperInvariant(), Normalize(path)));
    }

    /// <summary>
    /// True when the request must be denied (403) for an anonymous principal — the negation of
    /// <see cref="IsAllowed"/>. A request is blocked unless it is explicitly allowlisted.
    /// </summary>
    public static bool IsBlocked(string method, string path) => !IsAllowed(method, path);

    /// <summary>
    /// True when the request reaches the single billable model path (<c>POST /api/chat</c>) and
    /// must be metered against the per-subject/per-IP limits and the daily budget. No other
    /// anonymous-reachable route calls a model.
    /// </summary>
    public static bool IsBudgetedModelRoute(string method, string path) =>
        HttpMethods.IsPost(method)
        && string.Equals(Normalize(path), ChatRoute, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Length > 1 ? path.TrimEnd('/') : path;
}

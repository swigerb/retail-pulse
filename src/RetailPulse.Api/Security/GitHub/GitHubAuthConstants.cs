namespace RetailPulse.Api.Security.GitHub;

/// <summary>
/// Single source of truth for the GitHub authentication mode's identifiers: the normalized
/// provider name, the token claim types, the constrained-but-full app role/scope a verified
/// GitHub session carries, and the three narrowly-anonymous BFF endpoint routes.
///
/// GitHub mode is a backend-for-frontend (BFF) confidential OAuth flow. The GitHub provider
/// access token never reaches the browser: the backend performs the code→token exchange, validates
/// the token by calling <c>/user</c>, verifies a server-side allowlist, and only then mints the
/// app's OWN short-lived session token. These constants keep the endpoints, the token service, the
/// authorization policy, the normalizer, and the tests all reasoning about the same values.
/// </summary>
public static class GitHubAuthConstants
{
    /// <summary>The normalized provider name stamped on every GitHub principal/token.</summary>
    public const string ProviderName = "GitHub";

    /// <summary>Claim type that records the issuing provider on the session token.</summary>
    public const string ProviderClaimType = "provider";

    /// <summary>
    /// Informational-only claim carrying the GitHub login handle. It is NEVER identity — the login
    /// is mutable and a renamed account could impersonate another. Identity is the immutable numeric
    /// id in the <c>sub</c> claim (<see cref="SubjectPrefix"/>).
    /// </summary>
    public const string LoginClaimType = "github_login";

    /// <summary>
    /// Prefix for the immutable subject: <c>github:&lt;numeric id&gt;</c>. The numeric id is the only
    /// stable, non-reassignable GitHub identifier and is the sole basis for identity/authorization.
    /// </summary>
    public const string SubjectPrefix = "github:";

    /// <summary>
    /// App role a verified GitHub session carries. Full authenticated capabilities are acceptable in
    /// GitHub mode ONLY after server-side allowlist verification succeeds, so this is the same role
    /// the Entra boundary requires (<c>RetailPulse.User</c>).
    /// </summary>
    public const string DefaultRole = "RetailPulse.User";

    /// <summary>Delegated scope a verified GitHub session carries (matches the Entra API scope).</summary>
    public const string DefaultScope = "access_as_user";

    // ── BFF endpoint routes (the ONLY narrowly-anonymous GitHub surface) ──────────
    public const string StartRoute = "/api/auth/github/start";
    public const string CallbackRoute = "/api/auth/github/callback";
    public const string ExchangeRoute = "/api/auth/github/exchange";

    // ── Fixed GitHub endpoints (SSRF defense: these are the ONLY hosts/paths ever called) ──
    public const string AuthorizeUrl = "https://github.com/login/oauth/authorize";
    public const string AccessTokenUrl = "https://github.com/login/oauth/access_token";
    public const string UserApiUrl = "https://api.github.com/user";
    public const string OrgMembershipUrlFormat = "https://api.github.com/user/memberships/orgs/{0}";

    /// <summary>
    /// Cookie-name base for the browser-bound OAuth state cookie in HOSTED / secure mode. The final
    /// name is per-state (<see cref="GitHubStateCookie.NameFor"/>) so parallel login tabs never clash;
    /// the <c>__Host-</c> prefix pins Secure + Path=/ + no Domain, the strongest anti-fixation binding.
    /// </summary>
    public const string SecureStateCookieBase = "__Host-rp_gh_state_";

    /// <summary>
    /// Cookie-name base for the DEVELOPMENT-only insecure state cookie (plain HTTP, no <c>__Host-</c>
    /// prefix). Only used when <see cref="GitHubAuthOptions.RequireSecureCookies"/> is explicitly false,
    /// which is rejected at startup outside Development.
    /// </summary>
    public const string DevStateCookieBase = "rp_gh_state_";
}

namespace RetailPulse.Api.Security.GitHub;

/// <summary>The result of a server-side allowlist decision.</summary>
/// <param name="Allowed">True only when the user satisfies at least one configured allowlist rule.</param>
/// <param name="Reason">A short, non-sensitive reason (for audit/telemetry — never leaks a token).</param>
public readonly record struct GitHubAllowlistDecision(bool Allowed, string Reason);

/// <summary>
/// Server-side allowlist enforcement for GitHub mode. Verified STRICTLY on the backend after the
/// confidential exchange and the <c>/user</c> validation, and keyed on the IMMUTABLE numeric GitHub
/// user id — never the mutable login — so a renamed or re-created account cannot inherit another
/// account's access.
///
/// A user is admitted when ANY configured rule matches, evaluated cheapest-first:
/// <list type="number">
///   <item>the numeric id is in <see cref="GitHubAuthOptions.AllowedUserIds"/>;</item>
///   <item>the user is an ACTIVE member of any org in <see cref="GitHubAuthOptions.AllowedOrgs"/>,
///     confirmed via <c>/user/memberships/orgs/{org}</c> (requires the <c>read:org</c> scope).</item>
/// </list>
/// The mutable login handle is NEVER an access mechanism — a renamed or re-created account that lands on
/// a previously-allowed handle can never inherit access. Every failure mode fails CLOSED: an empty
/// allowlist (rejected at startup), an org API error, a rate-limit response, or an inactive/pending
/// membership all deny access. Org membership can be private; reading it requires <c>read:org</c>, which
/// is why that scope is requested only when an org allowlist is configured.
/// </summary>
public sealed class GitHubUserAllowlist
{
    private readonly GitHubAuthOptions _options;
    private readonly IGitHubOAuthClient _client;
    private readonly ILogger<GitHubUserAllowlist> _logger;

    public GitHubUserAllowlist(
        GitHubAuthOptions options,
        IGitHubOAuthClient client,
        ILogger<GitHubUserAllowlist> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Decides whether <paramref name="user"/> may be admitted. The provider <paramref name="accessToken"/>
    /// is used ONLY to read org membership when needed and is never stored, returned, or logged.
    /// </summary>
    public async Task<GitHubAllowlistDecision> EvaluateAsync(
        GitHubVerifiedUser user, string accessToken, CancellationToken cancellationToken)
    {
        if (user.UserId <= 0)
        {
            return new GitHubAllowlistDecision(false, "invalid_user");
        }

        // 1) Immutable numeric id allowlist — the strongest, cheapest check.
        if (_options.AllowedUserIds.Contains(user.UserId))
        {
            return new GitHubAllowlistDecision(true, "user_id");
        }

        // 2) Active org membership — fail closed on any API/rate/error and on non-active states.
        foreach (string org in _options.AllowedOrgs)
        {
            GitHubOrgMembershipResult membership =
                await _client.GetActiveOrgMembershipAsync(accessToken, org, cancellationToken);
            if (membership.IsActiveMember)
            {
                return new GitHubAllowlistDecision(true, "org_membership");
            }

            if (membership.Error is not null && !membership.Error.StartsWith("org_http_404", StringComparison.Ordinal)
                && membership.Error != "org_not_active")
            {
                // A genuine API/scope/rate error (not a plain "not a member" 404) — record it, but keep
                // evaluating remaining orgs before failing closed.
                _logger.LogWarning("GitHub org membership check for an allowlisted org returned {Reason}", membership.Error);
            }
        }

        return new GitHubAllowlistDecision(false, "not_allowlisted");
    }
}

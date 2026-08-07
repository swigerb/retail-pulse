using RetailPulse.Api.Security.GitHub;

namespace RetailPulse.Tests.Security;

/// <summary>
/// A programmable in-memory <see cref="IGitHubOAuthClient"/> test double. Tests set the exchange /
/// user / org-membership outcomes; the object records the provider token it was handed so tests can
/// assert the token is used only for verification and never leaks. No real HTTP is performed.
/// </summary>
internal sealed class FakeGitHubOAuthClient : IGitHubOAuthClient
{
    public Func<string, GitHubTokenResult> OnExchange { get; set; } =
        _ => new GitHubTokenResult(true, "gho_provider_token_secret", null);

    public Func<string, GitHubUserResult> OnGetUser { get; set; } =
        _ => new GitHubUserResult(true, 12345, "octocat", null);

    public Func<string, string, GitHubOrgMembershipResult> OnOrgMembership { get; set; } =
        (_, _) => new GitHubOrgMembershipResult(false, "org_http_404");

    public List<string> ExchangedCodes { get; } = [];
    public List<string> SeenTokens { get; } = [];
    public List<string> OrgChecks { get; } = [];

    public Task<GitHubTokenResult> ExchangeCodeAsync(string code, CancellationToken cancellationToken)
    {
        ExchangedCodes.Add(code);
        return Task.FromResult(OnExchange(code));
    }

    public Task<GitHubUserResult> GetUserAsync(string accessToken, CancellationToken cancellationToken)
    {
        SeenTokens.Add(accessToken);
        return Task.FromResult(OnGetUser(accessToken));
    }

    public Task<GitHubOrgMembershipResult> GetActiveOrgMembershipAsync(
        string accessToken, string org, CancellationToken cancellationToken)
    {
        SeenTokens.Add(accessToken);
        OrgChecks.Add(org);
        return Task.FromResult(OnOrgMembership(accessToken, org));
    }
}

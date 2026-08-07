using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Security.GitHub;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Server-side allowlist contract for GitHub mode. The allowlist is the gate that makes full
/// authenticated capabilities acceptable, so it is proven to key on the IMMUTABLE numeric id, to
/// admit via login or ACTIVE org membership, and to FAIL CLOSED on every error, inactive membership,
/// or non-member response.
/// </summary>
public sealed class GitHubUserAllowlistTests
{
    private sealed class TestEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "RetailPulse.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static GitHubAuthOptions Options(Dictionary<string, string?> extra)
    {
        var cfg = new Dictionary<string, string?>
        {
            ["GitHub:ClientId"] = "Iv1.abc",
            ["GitHub:ClientSecret"] = "secret",
            ["GitHub:SigningKey"] = "github-mode-test-signing-key-0123456789abcdef",
            ["GitHub:CallbackUrl"] = "https://api.example.com/api/auth/github/callback",
            ["GitHub:FrontendReturnUrl"] = "https://app.example.com/auth/github/callback",
        };
        foreach ((string k, string? v) in extra)
        {
            cfg[k] = v;
        }

        return GitHubAuthOptions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(cfg).Build(), new TestEnv());
    }

    private static GitHubUserAllowlist Allowlist(GitHubAuthOptions options, FakeGitHubOAuthClient client) =>
        new(options, client, NullLogger<GitHubUserAllowlist>.Instance);

    [Fact]
    public async Task NumericIdMatch_Allows()
    {
        GitHubAuthOptions options = Options(new() { ["GitHub:AllowedUserIds:0"] = "12345" });
        var client = new FakeGitHubOAuthClient();
        GitHubUserAllowlist allowlist = Allowlist(options, client);

        GitHubAllowlistDecision decision =
            await allowlist.EvaluateAsync(new GitHubVerifiedUser(12345, "octocat"), "tok", CancellationToken.None);

        decision.Allowed.Should().BeTrue();
        decision.Reason.Should().Be("user_id");
        client.OrgChecks.Should().BeEmpty("a numeric id match must not need an org API call");
    }

    [Fact]
    public async Task LoginMatch_IsCaseInsensitive_Allows()
    {
        GitHubAuthOptions options = Options(new() { ["GitHub:AllowedLogins:0"] = "OctoCat" });
        GitHubUserAllowlist allowlist = Allowlist(options, new FakeGitHubOAuthClient());

        GitHubAllowlistDecision decision =
            await allowlist.EvaluateAsync(new GitHubVerifiedUser(999, "octocat"), "tok", CancellationToken.None);

        decision.Allowed.Should().BeTrue();
        decision.Reason.Should().Be("login");
    }

    [Fact]
    public async Task ActiveOrgMembership_Allows()
    {
        GitHubAuthOptions options = Options(new() { ["GitHub:AllowedOrgs:0"] = "contoso" });
        var client = new FakeGitHubOAuthClient
        {
            OnOrgMembership = (_, org) => org == "contoso"
                ? new GitHubOrgMembershipResult(true, null)
                : new GitHubOrgMembershipResult(false, "org_http_404"),
        };
        GitHubUserAllowlist allowlist = Allowlist(options, client);

        GitHubAllowlistDecision decision =
            await allowlist.EvaluateAsync(new GitHubVerifiedUser(999, "octocat"), "tok", CancellationToken.None);

        decision.Allowed.Should().BeTrue();
        decision.Reason.Should().Be("org_membership");
    }

    [Fact]
    public async Task InactiveOrgMembership_FailsClosed()
    {
        GitHubAuthOptions options = Options(new() { ["GitHub:AllowedOrgs:0"] = "contoso" });
        var client = new FakeGitHubOAuthClient
        {
            OnOrgMembership = (_, _) => new GitHubOrgMembershipResult(false, "org_not_active"),
        };
        GitHubUserAllowlist allowlist = Allowlist(options, client);

        GitHubAllowlistDecision decision =
            await allowlist.EvaluateAsync(new GitHubVerifiedUser(999, "octocat"), "tok", CancellationToken.None);

        decision.Allowed.Should().BeFalse("a pending/inactive membership must not admit the user");
    }

    [Fact]
    public async Task OrgApiError_FailsClosed()
    {
        GitHubAuthOptions options = Options(new() { ["GitHub:AllowedOrgs:0"] = "contoso" });
        var client = new FakeGitHubOAuthClient
        {
            OnOrgMembership = (_, _) => new GitHubOrgMembershipResult(false, "org_http_403"),
        };
        GitHubUserAllowlist allowlist = Allowlist(options, client);

        GitHubAllowlistDecision decision =
            await allowlist.EvaluateAsync(new GitHubVerifiedUser(999, "octocat"), "tok", CancellationToken.None);

        decision.Allowed.Should().BeFalse("an org API/scope/rate error must fail closed");
    }

    [Fact]
    public async Task NoMatch_Denies()
    {
        GitHubAuthOptions options = Options(new() { ["GitHub:AllowedUserIds:0"] = "12345" });
        GitHubUserAllowlist allowlist = Allowlist(options, new FakeGitHubOAuthClient());

        GitHubAllowlistDecision decision =
            await allowlist.EvaluateAsync(new GitHubVerifiedUser(777, "someone-else"), "tok", CancellationToken.None);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be("not_allowlisted");
    }

    [Fact]
    public async Task InvalidUserId_Denies()
    {
        GitHubAuthOptions options = Options(new() { ["GitHub:AllowedUserIds:0"] = "12345" });
        GitHubUserAllowlist allowlist = Allowlist(options, new FakeGitHubOAuthClient());

        GitHubAllowlistDecision decision =
            await allowlist.EvaluateAsync(new GitHubVerifiedUser(0, "x"), "tok", CancellationToken.None);

        decision.Allowed.Should().BeFalse();
    }
}

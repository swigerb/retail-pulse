using FluentAssertions;

namespace RetailPulse.Tests.Deployment;

/// <summary>
/// Static deployment-contract guardrails for the provider-neutral authentication foundation.
/// These only read repo files — they never invoke azd or touch Azure.
///
/// They prove that the LIVE deployment path is explicitly pinned to the Entra authentication
/// mode and can never silently select GitHub or Anonymous:
/// <list type="bullet">
///   <item>Both azd postprovision hooks set <c>Authentication__Mode=Entra</c> on the API.</item>
///   <item>Neither hook ever sets the API mode to GitHub or Anonymous.</item>
///   <item>The committed <c>appsettings.Production.json</c> pins <c>Authentication:Mode = Entra</c>.</item>
/// </list>
/// </summary>
public sealed class ProviderNeutralDeploymentContractTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Theory]
    [InlineData("postprovision.ps1")]
    [InlineData("postprovision.sh")]
    public void PostprovisionHook_PinsApiAuthenticationModeToEntra(string hookFile)
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", hookFile));

        script.Should().Contain("Authentication__Mode=Entra",
            $"{hookFile} must explicitly pin the API to the Entra authentication mode (not merely default to it)");
    }

    [Theory]
    [InlineData("postprovision.ps1")]
    [InlineData("postprovision.sh")]
    public void PostprovisionHook_NeverSelectsGitHubOrAnonymousMode(string hookFile)
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", hookFile));

        script.Should().NotContain("Authentication__Mode=Anonymous",
            $"{hookFile} must never deploy the API in Anonymous authentication mode");
        script.Should().NotContain("Authentication__Mode=GitHub",
            $"{hookFile} must never deploy the API in GitHub authentication mode");
    }

    [Fact]
    public void ProductionAppSettings_PinsAuthenticationModeToEntra()
    {
        string json = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "RetailPulse.Api", "appsettings.Production.json"));

        json.Should().MatchRegex(
            "\"Authentication\"\\s*:\\s*\\{[^}]*\"Mode\"\\s*:\\s*\"Entra\"",
            "appsettings.Production.json must explicitly pin Authentication:Mode to Entra");
        json.Should().NotContain("\"Mode\": \"Anonymous\"");
        json.Should().NotContain("\"Mode\": \"GitHub\"");
    }

    [Fact]
    public void BaseAppSettings_DeclareEntraAuthenticationMode()
    {
        string json = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "RetailPulse.Api", "appsettings.json"));

        json.Should().MatchRegex(
            "\"Authentication\"\\s*:\\s*\\{[^}]*\"Mode\"\\s*:\\s*\"Entra\"",
            "the base appsettings.json must declare a deterministic Entra authentication mode");
    }

    [Fact]
    public void BaseAppSettings_NeverSelectsAnonymousOrGitHubMode()
    {
        string json = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "RetailPulse.Api", "appsettings.json"));

        json.Should().NotContain("\"Mode\": \"Anonymous\"",
            "the live base appsettings.json must never select Anonymous mode");
        json.Should().NotContain("\"Mode\": \"GitHub\"",
            "the live base appsettings.json must never select GitHub mode");
    }

    [Theory]
    [InlineData("postprovision.ps1")]
    [InlineData("postprovision.sh")]
    public void PostprovisionHook_NeverConfiguresAnonymousGuardrails(string hookFile)
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", hookFile));

        // The live deployment is Entra-only, so it must never carry the Anonymous hosted
        // opt-in or a signing key — configuring these would imply an Anonymous deployment.
        script.Should().NotContain("Anonymous__AllowHosted",
            $"{hookFile} must never enable hosted Anonymous mode");
        script.Should().NotContain("Anonymous__SigningKey",
            $"{hookFile} must never carry an Anonymous signing key");
    }

    [Fact]
    public void ContainerAppsBicep_PinsApiToSingleReplica()
    {
        // maxReplicas: 1 is required for the single SQLite writer AND for the (non-Production)
        // Anonymous mode's replica-local billable-use circuit breaker. This proves the live
        // artifact cannot scale the API out.
        string bicep = File.ReadAllText(Path.Combine(
            RepoRoot, "infra", "modules", "container-apps.bicep"));

        bicep.Should().MatchRegex(
            "maxReplicas\\s*:\\s*1",
            "the API container app must pin maxReplicas to 1");
    }

    [Fact]
    public void ExampleAnonymousConfig_IsNotLoadedByAnyEnvironment()
    {
        // The Anonymous example/template MUST NOT be a real environment overlay. Only
        // appsettings.json and appsettings.{Environment}.json are auto-loaded; the example
        // is deliberately named appsettings.Anonymous.example.json (note ".example.") so it
        // can never be picked up by ASPNETCORE_ENVIRONMENT=Anonymous.
        string apiDir = Path.Combine(RepoRoot, "src", "RetailPulse.Api");

        File.Exists(Path.Combine(apiDir, "appsettings.Anonymous.json"))
            .Should().BeFalse("a live appsettings.Anonymous.json overlay must not exist");
        File.Exists(Path.Combine(apiDir, "appsettings.Anonymous.example.json"))
            .Should().BeTrue("the non-live Anonymous template should be committed for documentation");
    }

    [Fact]
    public void ExampleGitHubConfig_IsNotLoadedByAnyEnvironment()
    {
        // The GitHub example/template MUST NOT be a real environment overlay. Only
        // appsettings.json and appsettings.{Environment}.json are auto-loaded; the example
        // is deliberately named appsettings.GitHub.example.json (note ".example.") so it
        // can never be picked up by ASPNETCORE_ENVIRONMENT=GitHub.
        string apiDir = Path.Combine(RepoRoot, "src", "RetailPulse.Api");

        File.Exists(Path.Combine(apiDir, "appsettings.GitHub.json"))
            .Should().BeFalse("a live appsettings.GitHub.json overlay must not exist");
        File.Exists(Path.Combine(apiDir, "appsettings.GitHub.example.json"))
            .Should().BeTrue("the non-live GitHub template should be committed for documentation");
    }

    [Fact]
    public void ExampleGitHubConfig_ContainsNoRealSecrets()
    {
        // The committed GitHub example must only ever carry angle-bracket placeholders for the
        // secret values — never a real client secret or signing key.
        string json = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "RetailPulse.Api", "appsettings.GitHub.example.json"));

        json.Should().MatchRegex("\"ClientSecret\"\\s*:\\s*\"<[^\"]*>\"",
            "the GitHub example client secret must be an angle-bracket placeholder");
        json.Should().MatchRegex("\"SigningKey\"\\s*:\\s*\"<[^\"]*>\"",
            "the GitHub example signing key must be an angle-bracket placeholder");
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RetailPulse.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate repo root (RetailPulse.slnx) walking up from " + AppContext.BaseDirectory);
    }
}

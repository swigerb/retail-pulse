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

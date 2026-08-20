using FluentAssertions;

namespace RetailPulse.Tests.Deployment;

/// <summary>
/// A16 — Content Safety deployment gate. When the feature is gated by
/// <c>contentSafetyEnabled = false</c> (the default), a clean <c>azd up</c>
/// must not provision a Content Safety account. When enabled, the module
/// must express the RBAC-first contract (managed identity, no local keys)
/// and the postprovision hooks must grant <c>Cognitive Services User</c> to
/// each container app system identity idempotently.
/// </summary>
public class ContentSafetyDeploymentContractTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void ContentSafetyModule_Exists()
    {
        File.Exists(Path.Combine(RepoRoot, "infra", "modules", "content-safety.bicep"))
            .Should().BeTrue("infra/modules/content-safety.bicep is the provisioning contract");
    }

    [Fact]
    public void ContentSafetyModule_UsesManagedIdentityAndDisablesLocalAuth()
    {
        string module = File.ReadAllText(Path.Combine(RepoRoot, "infra", "modules", "content-safety.bicep"));

        module.Should().Contain("kind: 'ContentSafety'",
            "the account must be a Content Safety Cognitive Services account");
        module.Should().Contain("type: 'SystemAssigned'",
            "the account must carry a system-assigned identity");
        module.Should().Contain("disableLocalAuth: true",
            "key-based auth must be disabled so callers must use managed identity");
        module.Should().NotContain("listKeys(",
            "the module must not surface keys anywhere");
    }

    [Fact]
    public void MainBicep_GatesContentSafetyBehindDisabledDefault()
    {
        string main = File.ReadAllText(Path.Combine(RepoRoot, "infra", "main.bicep"));

        main.Should().Contain("param contentSafetyEnabled bool = false",
            "Content Safety must ship disabled-by-default so a clean azd up is unchanged");
        main.Should().Contain("if (contentSafetyEnabled)",
            "the Content Safety module must be conditional on the enabled flag");
        main.Should().Contain("AZURE_CONTENT_SAFETY_ENDPOINT",
            "provisioning must publish the endpoint output so the API can consume it");
    }

    [Fact]
    public void BicepParam_ReadsContentSafetyToggleFromEnvironment()
    {
        string param = File.ReadAllText(Path.Combine(RepoRoot, "infra", "main.bicepparam"));

        param.Should().Contain("AZURE_CONTENT_SAFETY_ENABLED",
            "operators toggle Content Safety with azd env set AZURE_CONTENT_SAFETY_ENABLED=true");
    }

    [Theory]
    [InlineData("postprovision.ps1")]
    [InlineData("postprovision.sh")]
    public void PostprovisionHook_GrantsCognitiveServicesUserWhenEnabled(string hookFile)
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", hookFile));

        script.Should().Contain("AZURE_CONTENT_SAFETY_ENABLED",
            $"{hookFile} must gate the Content Safety role assignment on the enabled flag");
        script.Should().Contain("Cognitive Services User",
            $"{hookFile} must grant the Cognitive Services User role for managed-identity access");
        script.Should().Contain("AZURE_CONTENT_SAFETY_RESOURCE_ID",
            $"{hookFile} must scope the role assignment to the Content Safety account resource id");
        // The two hook dialects express az arguments differently — sh uses
        // space-separated tokens, pwsh splats a quoted comma-separated array.
        // Match either form so the assertion doesn't hard-code the dialect.
        (script.Contains("role assignment list") || script.Contains("'role', 'assignment', 'list'"))
            .Should().BeTrue($"{hookFile} must check for an existing grant so the hook stays idempotent");
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RetailPulse.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root walking up from " + AppContext.BaseDirectory);
    }
}

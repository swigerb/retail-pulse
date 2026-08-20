using FluentAssertions;

namespace RetailPulse.Tests.Deployment;

/// <summary>
/// Issue #103 — Azure AI Search deployment gate. When the feature is gated by
/// <c>aiSearchEnabled = false</c> (the default), a clean <c>azd up</c> must
/// not provision a Search service. When enabled, the module must express the
/// RBAC-first contract (managed identity, no local keys) and the postprovision
/// hooks must grant <c>Search Service Contributor</c> + <c>Search Index Data
/// Contributor</c> to every container app system identity idempotently.
/// </summary>
public class AzureAISearchDeploymentContractTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void AiSearchModule_Exists()
    {
        File.Exists(Path.Combine(RepoRoot, "infra", "modules", "ai-search.bicep"))
            .Should().BeTrue("infra/modules/ai-search.bicep is the provisioning contract");
    }

    [Fact]
    public void AiSearchModule_UsesManagedIdentityAndDisablesLocalAuth()
    {
        string module = File.ReadAllText(Path.Combine(RepoRoot, "infra", "modules", "ai-search.bicep"));

        module.Should().Contain("Microsoft.Search/searchServices",
            "the module must declare an Azure AI Search service");
        module.Should().Contain("type: 'SystemAssigned'",
            "the service must carry a system-assigned identity");
        module.Should().Contain("disableLocalAuth: true",
            "key-based auth must be disabled so callers must use managed identity");
        module.Should().NotContain("listAdminKeys(",
            "the module must not surface admin keys");
        module.Should().NotContain("listQueryKeys(",
            "the module must not surface query keys");
    }

    [Fact]
    public void MainBicep_GatesAiSearchBehindDisabledDefault()
    {
        string main = File.ReadAllText(Path.Combine(RepoRoot, "infra", "main.bicep"));

        main.Should().Contain("param aiSearchEnabled bool = false",
            "AI Search must ship disabled-by-default so a clean azd up is unchanged");
        main.Should().Contain("if (aiSearchEnabled)",
            "the AI Search module must be conditional on the enabled flag");
        main.Should().Contain("AZURE_AI_SEARCH_ENDPOINT",
            "provisioning must publish the endpoint output so the API can consume it");
        main.Should().Contain("AZURE_AI_SEARCH_RESOURCE_ID",
            "provisioning must publish the resource id so the postprovision hook can scope RBAC");
    }

    [Fact]
    public void BicepParam_ReadsAiSearchToggleFromEnvironment()
    {
        string param = File.ReadAllText(Path.Combine(RepoRoot, "infra", "main.bicepparam"));

        param.Should().Contain("AZURE_AI_SEARCH_ENABLED",
            "operators toggle AI Search with azd env set AZURE_AI_SEARCH_ENABLED=true");
    }

    [Theory]
    [InlineData("postprovision.ps1")]
    [InlineData("postprovision.sh")]
    public void PostprovisionHook_GrantsSearchRolesWhenEnabled(string hookFile)
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", hookFile));

        script.Should().Contain("AZURE_AI_SEARCH_ENABLED",
            $"{hookFile} must gate the AI Search role assignments on the enabled flag");
        script.Should().Contain("Search Service Contributor",
            $"{hookFile} must grant Search Service Contributor for index create/inspect");
        script.Should().Contain("Search Index Data Contributor",
            $"{hookFile} must grant Search Index Data Contributor for document CRUD");
        script.Should().Contain("AZURE_AI_SEARCH_RESOURCE_ID",
            $"{hookFile} must scope the role assignments to the Search service resource id");
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

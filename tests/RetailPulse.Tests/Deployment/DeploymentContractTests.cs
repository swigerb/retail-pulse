using System.Text.RegularExpressions;
using FluentAssertions;
using YamlDotNet.RepresentationModel;

namespace RetailPulse.Tests.Deployment;

/// <summary>
/// Static guardrails around the <c>azd</c> deployment contract. These tests only
/// inspect repo files (<c>azure.yaml</c>, the azd hooks, and the infra Bicep) —
/// they never invoke <c>azd</c> or touch Azure.
///
/// They exist because a clean or repeated <c>azd up</c> must be self-contained:
/// a dedicated ACR must be provisioned and its endpoint/name/resource-id emitted
/// as azd outputs, and the cross-platform postprovision hooks must idempotently
/// bind every Container App's system-assigned identity to <c>AcrPull</c> (no
/// registry secrets). Regressions here reproduce the <c>UNAUTHORIZED</c>
/// image-pull failure from issue #11's live deployment.
/// </summary>
public partial class DeploymentContractTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"type:\s*'SystemAssigned'")]
    private static partial Regex SystemAssignedIdentityRegex();

    // ── azure.yaml hook wiring ──────────────────────────────────────────────

    [Fact]
    public void AzureYaml_PreprovisionHook_RemainsWiredCrossPlatform()
    {
        YamlMappingNode hooks = HooksNode();
        AssertHookWired(hooks, "preprovision", "./azd-hooks/preprovision.ps1", "./azd-hooks/preprovision.sh");
    }

    [Fact]
    public void AzureYaml_PostprovisionHook_IsWiredCrossPlatform()
    {
        YamlMappingNode hooks = HooksNode();
        AssertHookWired(hooks, "postprovision", "./azd-hooks/postprovision.ps1", "./azd-hooks/postprovision.sh");
    }

    private static void AssertHookWired(YamlMappingNode hooks, string hookName, string windowsRun, string posixRun)
    {
        YamlNode? hook = GetChild(hooks, hookName);
        hook.Should().NotBeNull($"azure.yaml hooks must declare '{hookName}'");
        var hookMap = (YamlMappingNode)hook;

        var windows = (YamlMappingNode?)GetChild(hookMap, "windows");
        windows.Should().NotBeNull($"'{hookName}' must have a windows variant");
        Scalar(windows, "shell").Should().Be("pwsh", $"'{hookName}' windows variant must run under pwsh");
        Scalar(windows, "run").Should().Be(windowsRun);

        var posix = (YamlMappingNode?)GetChild(hookMap, "posix");
        posix.Should().NotBeNull($"'{hookName}' must have a posix variant");
        Scalar(posix, "shell").Should().Be("sh", $"'{hookName}' posix variant must run under sh");
        Scalar(posix, "run").Should().Be(posixRun);
    }

    // ── Hook scripts exist, are cross-platform, and do the right work ────────

    [Fact]
    public void PostprovisionHooks_Exist()
    {
        File.Exists(Path.Combine(RepoRoot, "azd-hooks", "postprovision.ps1")).Should().BeTrue();
        File.Exists(Path.Combine(RepoRoot, "azd-hooks", "postprovision.sh")).Should().BeTrue();
    }

    [Fact]
    public void PostprovisionShellHook_UsesLfLineEndings()
    {
        // POSIX hooks must be LF-only; a CR byte breaks execution under /bin/sh.
        byte[] bytes = File.ReadAllBytes(Path.Combine(RepoRoot, "azd-hooks", "postprovision.sh"));
        bytes.Should().NotContain((byte)'\r', "postprovision.sh must use LF line endings for POSIX shells");
    }

    [Theory]
    [InlineData("postprovision.ps1")]
    [InlineData("postprovision.sh")]
    public void PostprovisionHook_GrantsAcrPullViaSystemIdentityWithoutSecrets(string hookFile)
    {
        string raw = File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", hookFile));

        // The two hook dialects express az arguments differently — sh uses
        // space-separated tokens, pwsh uses quoted/comma-separated array
        // splatting. Normalize both into a single token stream so the assertions
        // below match the command shape regardless of syntax.
        string script = Normalize(raw);

        script.Should().Contain("AcrPull",
            $"{hookFile} must grant the AcrPull role to each container app identity");
        script.Should().Contain("--assignee-principal-type ServicePrincipal",
            $"{hookFile} must scope the AcrPull grant to the app's managed identity");
        script.Should().Contain("containerapp registry set",
            $"{hookFile} must configure each app's registry auth");
        script.Should().Contain("--identity system",
            $"{hookFile} must bind registry auth to the system-assigned identity");
        script.Should().Contain("role assignment list",
            $"{hookFile} must check for an existing AcrPull grant so the hook is idempotent");

        // Identity-based auth only — never fall back to registry admin credentials.
        script.Should().NotContain("--username",
            $"{hookFile} must not configure registry credentials/secrets");
        script.Should().NotContain("--password",
            $"{hookFile} must not configure registry credentials/secrets");
    }

    [Theory]
    [InlineData("postprovision.ps1")]
    [InlineData("postprovision.sh")]
    public void PostprovisionHook_DerivesValuesFromAzdEnvironmentOutputs(string hookFile)
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", hookFile));

        foreach (string requiredEnv in new[]
                 {
                     "AZURE_RESOURCE_GROUP",
                     "AZURE_CONTAINER_REGISTRY_NAME",
                     "AZURE_CONTAINER_REGISTRY_ENDPOINT",
                     "AZURE_CONTAINER_REGISTRY_RESOURCE_ID",
                     "AZURE_API_APP_NAME",
                     "AZURE_API_APP_URL",
                     "AZURE_MCP_SERVER_APP_NAME",
                     "AZURE_MCP_SERVER_APP_URL",
                     "AZURE_TEAMS_BOT_APP_NAME",
                     "AZURE_OPENAI_ENDPOINT",
                     "RETAIL_PULSE_FRONTEND_ORIGIN",
                     "AZURE_STATIC_WEB_APP_NAME",
                     "AZURE_LOCATION",
                 })
        {
            script.Should().Contain(requiredEnv,
                $"{hookFile} must derive '{requiredEnv}' from the azd environment (a main.bicep output)");
        }
    }

    [Theory]
    [InlineData("postprovision.ps1")]
    [InlineData("postprovision.sh")]
    public void PostprovisionHook_LinksRestApiBeforeDisablingPlatformAuth(string hookFile)
    {
        string script = Normalize(File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", hookFile)));

        int linkIndex = script.IndexOf("staticwebapp backends link", StringComparison.Ordinal);
        int authIndex = script.IndexOf("containerapp auth update", StringComparison.Ordinal);

        linkIndex.Should().BeGreaterThanOrEqualTo(0,
            $"{hookFile} must link SWA relative /api requests to the ACA API");
        authIndex.Should().BeGreaterThan(linkIndex,
            $"{hookFile} must disable platform auth after linking because the link enables the SWA identity provider");
    }

    // ── main.bicep: dedicated registry + azd-consumed outputs ───────────────

    [Fact]
    public void MainBicep_WiresDedicatedContainerRegistryModule()
    {
        string bicep = File.ReadAllText(Path.Combine(RepoRoot, "infra", "main.bicep"));
        bicep.Should().Contain("./modules/container-registry.bicep",
            "main.bicep must provision a dedicated container registry module");
    }

    [Theory]
    [InlineData("AZURE_CONTAINER_REGISTRY_ENDPOINT")]
    [InlineData("AZURE_CONTAINER_REGISTRY_NAME")]
    [InlineData("AZURE_CONTAINER_REGISTRY_RESOURCE_ID")]
    public void MainBicep_EmitsRegistryOutput_AzdConsumes(string outputName)
    {
        string bicep = File.ReadAllText(Path.Combine(RepoRoot, "infra", "main.bicep"));
        var pattern = new Regex($@"output\s+{Regex.Escape(outputName)}\s+string\s*=", RegexOptions.Multiline);
        pattern.IsMatch(bicep).Should().BeTrue(
            $"main.bicep must emit '{outputName}' so azd can push/pull against the dedicated registry");
    }

    // ── container-registry module: Basic SKU, no admin secrets ──────────────

    [Fact]
    public void ContainerRegistryModule_UsesBasicSkuWithoutAdminUser()
    {
        string path = Path.Combine(RepoRoot, "infra", "modules", "container-registry.bicep");
        File.Exists(path).Should().BeTrue("the dedicated ACR module must exist");

        string bicep = File.ReadAllText(path);
        bicep.Should().MatchRegex(@"Microsoft\.ContainerRegistry/registries",
            "the module must declare an Azure Container Registry");
        bicep.Should().MatchRegex(@"name:\s*'Basic'",
            "the registry must use the Basic SKU per the deployment design");
        bicep.Should().MatchRegex(@"adminUserEnabled:\s*false",
            "admin credentials must stay disabled — apps pull via managed identity");
    }

    // ── container apps keep system-assigned identity (issue #11 intact) ──────

    [Fact]
    public void ContainerApps_AllUseSystemAssignedIdentity()
    {
        string bicep = File.ReadAllText(Path.Combine(RepoRoot, "infra", "modules", "container-apps.bicep"));
        int systemAssigned = SystemAssignedIdentityRegex().Count(bicep);
        systemAssigned.Should().Be(3,
            "all three container apps (api, mcp, teamsbot) must use system-assigned identities for secretless ACR pull");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static YamlMappingNode HooksNode()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot, "azure.yaml"));
        var stream = new YamlStream();
        using var reader = new StringReader(text);
        stream.Load(reader);

        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        YamlNode? hooks = GetChild(root, "hooks");
        hooks.Should().NotBeNull("azure.yaml must declare a hooks section");
        return (YamlMappingNode)hooks;
    }

    private static string Normalize(string script)
    {
        // Reduce both hook dialects to a space-separated token stream: drop
        // quotes, commas and pwsh line-continuation backticks so array-splatted
        // args ('a', 'b') and shell args (a b) compare identically.
        string cleaned = script
            .Replace("'", " ")
            .Replace("\"", " ")
            .Replace(",", " ")
            .Replace("`", " ");
        return WhitespaceRegex().Replace(cleaned, " ");
    }

    private static YamlNode? GetChild(YamlMappingNode map, string key)
    {
        foreach (KeyValuePair<YamlNode, YamlNode> kv in map.Children)
        {
            if (kv.Key is YamlScalarNode scalar && scalar.Value == key)
            {
                return kv.Value;
            }
        }

        return null;
    }

    private static string Scalar(YamlMappingNode map, string key)
    {
        var node = GetChild(map, key) as YamlScalarNode;
        node.Should().NotBeNull($"expected scalar '{key}'");
        return node.Value ?? string.Empty;
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
            "Could not locate repo root (RetailPulse.slnx) walking up from " +
            AppContext.BaseDirectory);
    }
}

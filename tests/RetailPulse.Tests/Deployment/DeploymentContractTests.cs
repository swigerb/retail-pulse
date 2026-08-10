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
                     "AZURE_APIM_NAME",
                     "AZURE_APIM_INFERENCE_ENDPOINT",
                     "AZURE_APIM_INFERENCE_SUBSCRIPTION_NAME",
                     "RETAIL_PULSE_FRONTEND_ORIGIN",
                     "AZURE_STATIC_WEB_APP_NAME",
                     "AZURE_LOCATION",
                     "RETAIL_PULSE_ENTRA_TENANT_ID",
                     "RETAIL_PULSE_ENTRA_CLIENT_ID",
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

    // ── API deploys as authenticated Production (issue: anonymous prod) ──────

    [Theory]
    [InlineData("postprovision.ps1")]
    [InlineData("postprovision.sh")]
    public void PostprovisionHook_DeploysApiAsAuthenticatedProduction(string hookFile)
    {
        string script = Normalize(File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", hookFile)));

        // The API must run real auth in Production. These are the exact settings that
        // were dangerously wrong in the live deployment (anonymous, Development mode).
        script.Should().Contain("Security__RequireAuth=true",
            $"{hookFile} must deploy the API with real auth enabled");
        script.Should().NotContain("Security__RequireAuth=false",
            $"{hookFile} must never ship the API with auth disabled (anonymous production)");
        script.Should().Contain("ASPNETCORE_ENVIRONMENT=Production",
            $"{hookFile} must deploy the API in the Production environment");

        // Tenant-scoped Entra values must be injected so JwtBearer can validate tokens.
        script.Should().Contain("MicrosoftEntra__TenantId=",
            $"{hookFile} must inject the Entra tenant id into the API");
        script.Should().Contain("MicrosoftEntra__ClientId=",
            $"{hookFile} must inject the Entra client id/audience into the API");
    }

    [Theory]
    [InlineData("postprovision.ps1")]
    [InlineData("postprovision.sh")]
    public void PostprovisionHook_WiresApiThroughApimGatewayUsingSecretReference(string hookFile)
    {
        string script = Normalize(File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", hookFile)));

        script.Should().Contain("listSecrets",
            $"{hookFile} must retrieve the APIM subscription key live instead of persisting it in azd outputs");
        script.Should().Contain("containerapp secret set",
            $"{hookFile} must store the APIM subscription key as a Container Apps secret");
        script.Should().Contain("OpenAI__Endpoint=",
            $"{hookFile} must update the API to point at the APIM inference endpoint");
        script.Should().Contain("AZURE_APIM_INFERENCE_ENDPOINT",
            $"{hookFile} must source the APIM inference endpoint from azd environment outputs");
        script.Should().Contain("OpenAI__UseManagedIdentity=false",
            $"{hookFile} must disable direct managed-identity auth when routing through APIM");
        script.Should().Contain("apim-sub-key",
            $"{hookFile} must use a stable Container Apps secret name for the APIM subscription key");
        script.Should().Contain("OpenAI__ApimSubscriptionKey=secretref:",
            $"{hookFile} must wire the API to the APIM subscription key secret reference");
    }

    [Theory]
    [InlineData("postprovision.ps1")]
    [InlineData("postprovision.sh")]
    public void PostprovisionHook_FailsFastWhenEntraConfigMissing(string hookFile)
    {
        // The Entra tenant/client values are read as REQUIRED azd env values so a
        // misconfigured deploy fails loudly instead of silently shipping anonymous.
        string raw = File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", hookFile));
        string script = Normalize(raw);

        script.Should().Contain("RETAIL_PULSE_ENTRA_TENANT_ID",
            $"{hookFile} must require the Entra tenant id from the azd environment");
        script.Should().Contain("RETAIL_PULSE_ENTRA_CLIENT_ID",
            $"{hookFile} must require the Entra client id from the azd environment");

        if (hookFile.EndsWith(".sh", StringComparison.Ordinal))
        {
            script.Should().Contain("require_env RETAIL_PULSE_ENTRA_TENANT_ID",
                $"{hookFile} must fail fast (require_env) when the Entra tenant id is missing");
            script.Should().Contain("require_env RETAIL_PULSE_ENTRA_CLIENT_ID",
                $"{hookFile} must fail fast (require_env) when the Entra client id is missing");
        }
        else
        {
            script.Should().Contain("Get-RequiredEnv RETAIL_PULSE_ENTRA_TENANT_ID",
                $"{hookFile} must fail fast (Get-RequiredEnv) when the Entra tenant id is missing");
            script.Should().Contain("Get-RequiredEnv RETAIL_PULSE_ENTRA_CLIENT_ID",
                $"{hookFile} must fail fast (Get-RequiredEnv) when the Entra client id is missing");
        }
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

    [Theory]
    [InlineData("VITE_ENTRA_TENANT_ID")]
    [InlineData("VITE_ENTRA_CLIENT_ID")]
    [InlineData("VITE_ENTRA_API_SCOPE")]
    [InlineData("VITE_ENTRA_AUDIENCE")]
    public void MainBicep_EmitsEntraViteOutput_ForFrontendBuild(string outputName)
    {
        string bicep = File.ReadAllText(Path.Combine(RepoRoot, "infra", "main.bicep"));
        var pattern = new Regex($@"output\s+{Regex.Escape(outputName)}\s+string\s*=", RegexOptions.Multiline);
        pattern.IsMatch(bicep).Should().BeTrue(
            $"main.bicep must emit '{outputName}' so the Vite frontend build embeds the Entra config into the SPA");
    }

    [Theory]
    [InlineData("RETAIL_PULSE_ENTRA_TENANT_ID")]
    [InlineData("RETAIL_PULSE_ENTRA_CLIENT_ID")]
    public void MainBicepParam_ReadsEntraEnv_NonSecret(string envVar)
    {
        string bicepparam = File.ReadAllText(Path.Combine(RepoRoot, "infra", "main.bicepparam"));
        bicepparam.Should().Contain($"readEnvironmentVariable('{envVar}'",
            $"main.bicepparam must source '{envVar}' from the azd environment so provisioning stays non-secret and idempotent");
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

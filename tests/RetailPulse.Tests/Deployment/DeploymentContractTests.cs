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

    [Fact]
    public void AzureYaml_PredeployHook_IsWiredCrossPlatform()
    {
        // Fixes the shared RetailPulse.ServiceDefaults sourcelink.json publish
        // race (issue #67): api/mcpserver/teamsbot publish in parallel and all
        // three write into the same shared project's obj/ output. A single
        // sequential solution build here, before azd's parallel per-service
        // publish, means MSBuild sees the shared project as already built and
        // skips regenerating it — no concurrent writers, no race.
        YamlMappingNode hooks = HooksNode();
        AssertHookWired(hooks, "predeploy", "./azd-hooks/predeploy.ps1", "./azd-hooks/predeploy.sh");
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

    // ── Mandatory APIM AI Gateway live gate (issue #67) ──────────────────────
    //
    // A successful `azd provision` only proves the ARM deployments succeeded —
    // it says nothing about whether the AI Gateway invariants (backend, policy,
    // token-limit, emit-token-metric, diagnostics, RBAC, ACA wiring) are correct
    // on the live resources. Prior to this fix, Verify-ApimAiGateway.ps1 was an
    // optional manual script nobody was required to run, which is how the #67
    // P0 slipped through a "successful" `azd up`. These tests pin the hooks to
    // invoke it as a hard postprovision gate.

    [Theory]
    [InlineData("postprovision.ps1")]
    [InlineData("postprovision.sh")]
    public void PostprovisionHook_InvokesMandatoryApimAiGatewayVerifier(string hookFile)
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", hookFile));

        script.Should().Contain("Verify-ApimAiGateway.ps1",
            $"{hookFile} must invoke scripts/Verify-ApimAiGateway.ps1 as a mandatory postprovision gate");
    }

    [Fact]
    public void PostprovisionPs1Hook_FailsProvisionOnVerifierFailure_ButNotOnSkip()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", "postprovision.ps1"));

        // Exit 0 = pass, exit 2 = environment-precondition skip (no az / not
        // signed in / missing required azd outputs) — only those two paths may
        // avoid throwing. Any other exit code (in particular exit 1, "one or
        // more live invariants failed") must `throw`, which pwsh propagates as
        // a non-zero hook exit and fails `azd provision`/`azd up` itself.
        script.Should().MatchRegex(
            @"if\s*\(\s*\$verifyExitCode\s+-eq\s+0\s*\)",
            "postprovision.ps1 must branch on the verifier's exit code, treating 0 as pass");
        script.Should().MatchRegex(
            @"elseif\s*\(\s*\$verifyExitCode\s+-eq\s+2\s*\)",
            "postprovision.ps1 must treat exit 2 (environment precondition, e.g. not signed in) as skip, not failure");
        script.Should().Contain("throw",
            "postprovision.ps1 must throw (fail the hook / provision) on any verifier exit code other than 0 or 2");
    }

    [Fact]
    public void PostprovisionShHook_FailsProvisionOnVerifierFailure_ButNotOnSkip()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", "postprovision.sh"));

        script.Should().MatchRegex(
            @"if\s*\[\s*""\$verify_exit_code""\s+-eq\s+0\s*\]",
            "postprovision.sh must branch on the verifier's exit code, treating 0 as pass");
        script.Should().MatchRegex(
            @"elif\s*\[\s*""\$verify_exit_code""\s+-eq\s+2\s*\]",
            "postprovision.sh must treat exit 2 (environment precondition) as skip, not failure");
        script.Should().Contain("exit 1",
            "postprovision.sh must exit non-zero (fail the hook / provision) on any verifier exit code other than 0 or 2");
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

        // The postprovision hook now handles only the steps that MUST run after Bicep:
        // the ACR AcrPull grant + system-identity registry bind, the SWA linked-backend,
        // and the ACA platform-auth disable. The API's APIM/Entra runtime env moved into
        // container-apps.bicep (see DeployedApi_IsWiredThroughApimGatewayUsingSecretReference
        // and DeployedApi_IsDeployedAsAuthenticatedProduction). These are the values the hook
        // still needs to derive from the azd environment.
        foreach (string requiredEnv in new[]
                 {
                     "AZURE_RESOURCE_GROUP",
                     "AZURE_CONTAINER_REGISTRY_NAME",
                     "AZURE_CONTAINER_REGISTRY_ENDPOINT",
                     "AZURE_CONTAINER_REGISTRY_RESOURCE_ID",
                     "AZURE_API_APP_NAME",
                     "AZURE_MCP_SERVER_APP_NAME",
                     "AZURE_TEAMS_BOT_APP_NAME",
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

    // ── Predeploy hook: shared-project publish race fix (issue #67) ──────────
    //
    // `azd up`/`azd deploy` publishes the api, mcpserver, and teamsbot container
    // app services in parallel. All three reference the shared
    // RetailPulse.ServiceDefaults project; each parallel `dotnet publish`
    // independently rebuilds it and writes its generated
    // RetailPulse.ServiceDefaults.sourcelink.json into the same shared obj/
    // directory — three concurrent writers racing on one file, causing
    // intermittent publish failures. The predeploy hook runs one sequential
    // `dotnet restore` + `dotnet build` of the whole solution first, so the
    // shared project is already up to date before the parallel publish phase
    // starts and MSBuild skips regenerating it.

    [Theory]
    [InlineData("predeploy.ps1")]
    [InlineData("predeploy.sh")]
    public void PredeployHook_Exists(string hookFile)
    {
        File.Exists(Path.Combine(RepoRoot, "azd-hooks", hookFile)).Should().BeTrue(
            $"azd-hooks/{hookFile} must exist to serialize the shared-project build before azd's parallel per-service publish");
    }

    [Fact]
    public void PredeployShellHook_UsesLfLineEndings()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(RepoRoot, "azd-hooks", "predeploy.sh"));
        bytes.Should().NotContain((byte)'\r', "predeploy.sh must use LF line endings for POSIX shells");
    }

    [Theory]
    [InlineData("predeploy.ps1")]
    [InlineData("predeploy.sh")]
    public void PredeployHook_BuildsWholeSolutionSequentially_BeforeParallelPublish(string hookFile)
    {
        string script = Normalize(File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", hookFile)));

        script.Should().Contain("dotnet restore",
            $"{hookFile} must restore the whole solution once before any service publish");
        script.Should().Contain("dotnet build",
            $"{hookFile} must build the whole solution once before any service publish");
        script.Should().Contain("RetailPulse.slnx",
            $"{hookFile} must target the full solution file (not a single project) so every project sharing " +
            "RetailPulse.ServiceDefaults is pre-built before the parallel per-service publish races on it");

        int restoreIndex = script.IndexOf("dotnet restore", StringComparison.Ordinal);
        int buildIndex = script.IndexOf("dotnet build", StringComparison.Ordinal);
        buildIndex.Should().BeGreaterThan(restoreIndex,
            $"{hookFile} must restore before building");
    }

    // ── API runtime config is declared in container-apps.bicep (idempotent) ──
    //
    // The API's APIM AI-Gateway wiring and Production Entra auth used to live in
    // the postprovision hook as `az containerapp update --set-env-vars`. That
    // pattern is not re-asserted by `azd provision`, so a subsequent provision
    // that recreated the ACA resource from Bicep would drop the AI-Gateway env
    // vars off the active revision (issue #51 §7). Both concerns now live in
    // infra/modules/container-apps.bicep so every `azd provision` re-emits them
    // declaratively. These tests keep the contract on the new source of truth.

    [Fact]
    public void DeployedApi_IsDeployedAsAuthenticatedProduction()
    {
        string bicep = File.ReadAllText(Path.Combine(
            RepoRoot, "infra", "modules", "container-apps.bicep"));

        bicep.Should().Contain("'Security__RequireAuth'",
            "container-apps.bicep must set Security__RequireAuth on the API");
        bicep.Should().MatchRegex(
            "'Security__RequireAuth'\\s*[^}]*value:\\s*'true'",
            "the API's Security__RequireAuth must be 'true' (never anonymous production)");
        bicep.Should().MatchRegex(
            "'ASPNETCORE_ENVIRONMENT'\\s*[^}]*value:\\s*'Production'",
            "the API must deploy in the Production ASP.NET Core environment");
        bicep.Should().Contain("'MicrosoftEntra__TenantId'",
            "container-apps.bicep must inject the Entra tenant id into the API");
        bicep.Should().Contain("'MicrosoftEntra__ClientId'",
            "container-apps.bicep must inject the Entra client id/audience into the API");
    }

    [Fact]
    public void DeployedApi_IsWiredThroughApimGatewayUsingSecretReference()
    {
        string bicep = File.ReadAllText(Path.Combine(
            RepoRoot, "infra", "modules", "container-apps.bicep"));

        bicep.Should().Contain("apim-sub-key",
            "container-apps.bicep must declare a stable ACA secret name for the APIM subscription key");
        bicep.Should().Contain("apimSubscriptionKey",
            "container-apps.bicep must consume the APIM subscription key at Bicep-time (via listSecrets on the module output)");
        bicep.Should().MatchRegex(
            "'OpenAI__ApimSubscriptionKey'\\s*[^}]*secretRef:\\s*apimSubscriptionKeySecretName",
            "the API must reference the APIM subscription key via a secretRef, never inline");
        bicep.Should().MatchRegex(
            "'OpenAI__Endpoint'\\s*[^}]*value:\\s*apimInferenceEndpoint",
            "the API must send inference calls to the APIM inference endpoint (not direct AOAI)");
        bicep.Should().MatchRegex(
            "'OpenAI__UseManagedIdentity'\\s*[^}]*value:\\s*'false'",
            "the API must disable direct managed-identity auth when routing through APIM");

        // The APIM primary key flows in from apim-openai-api.bicep as a @secure() output.
        string apimApi = File.ReadAllText(Path.Combine(
            RepoRoot, "infra", "modules", "apim-openai-api.bicep"));
        apimApi.Should().Contain("subscription.listSecrets()",
            "apim-openai-api.bicep must resolve the APIM subscription primary key via listSecrets()");
        apimApi.Should().MatchRegex(
            "@secure\\(\\)\\s*output\\s+subscriptionKey\\s+string",
            "apim-openai-api.bicep must expose the APIM subscription key as a @secure() output");
    }

    [Fact]
    public void ApimOpenAiApiBicep_InferenceEndpointOutputDoesNotDoubleAppendOpenAiSegment()
    {
        // Root cause of the 2026-08-11 production incident: the APIM API's registered
        // path is '{inferenceApiPath}/openai' (so its OpenAPI import matches AOAI's real
        // '/openai/deployments/...' route shape), but Azure.AI.OpenAI's AzureOpenAIClient
        // itself appends '/openai/deployments/{id}/...' to whatever endpoint it is given.
        // If `inferenceEndpoint` echoed the API's full registered path (including the
        // trailing '/openai'), the SDK would double it up into
        // '.../inference/openai/openai/deployments/...', which APIM rejects as 404
        // OperationNotFound before any agent/tool execution starts. The output must use
        // only the base `inferenceApiPath` (e.g. '.../inference'), never
        // `api.properties.path` (e.g. '.../inference/openai').
        string apimApi = File.ReadAllText(Path.Combine(
            RepoRoot, "infra", "modules", "apim-openai-api.bicep"));

        apimApi.Should().Contain(
            "output inferenceEndpoint string = '${apim.properties.gatewayUrl}/${inferenceApiPath}'",
            "inferenceEndpoint must be built from the base inferenceApiPath param, not api.properties.path, " +
            "so the AzureOpenAIClient's own '/openai/deployments/...' suffix does not get doubled");
        apimApi.Should().NotMatchRegex(
            @"output\s+inferenceEndpoint\s+string\s*=\s*'\$\{apim\.properties\.gatewayUrl\}/\$\{api\.properties\.path\}'",
            "inferenceEndpoint must not be derived from api.properties.path (that path already ends in " +
            "'/openai', which combined with the SDK's own suffix produces a double '/openai/openai/' segment)");
    }

    [Fact]
    public void MainBicep_PipesApimAndSwaOutputsThroughToContainerApps()
    {
        // Ordering matters: staticWebApp + apimOpenAiApi must run BEFORE containerApps so
        // their outputs (frontend origin, APIM inference endpoint, APIM subscription key)
        // can flow into the API's declarative env. This is the ordering that closes the
        // §7 regression on issue #51.
        string bicep = File.ReadAllText(Path.Combine(RepoRoot, "infra", "main.bicep"));

        int staticIndex = bicep.IndexOf("module staticWebApp ", StringComparison.Ordinal);
        int apimOpenAiIndex = bicep.IndexOf("module apimOpenAiApi ", StringComparison.Ordinal);
        int containerAppsIndex = bicep.IndexOf("module containerApps ", StringComparison.Ordinal);

        staticIndex.Should().BeGreaterThan(-1);
        apimOpenAiIndex.Should().BeGreaterThan(-1);
        containerAppsIndex.Should().BeGreaterThan(-1);
        staticIndex.Should().BeLessThan(containerAppsIndex,
            "staticWebApp must be declared before containerApps so frontendOrigin can flow into the API");
        apimOpenAiIndex.Should().BeLessThan(containerAppsIndex,
            "apimOpenAiApi must be declared before containerApps so the APIM endpoint + key can flow into the API");

        bicep.Should().Contain("apimInferenceEndpoint: apimOpenAiApi.outputs.inferenceEndpoint",
            "main.bicep must pipe the APIM inference endpoint into the containerApps module");
        bicep.Should().Contain("apimSubscriptionKey: apimOpenAiApi.outputs.subscriptionKey",
            "main.bicep must pipe the APIM subscription key into the containerApps module");
        bicep.Should().Contain("frontendOrigin: staticWebApp.outputs.staticWebAppUrl",
            "main.bicep must pipe the Static Web App origin into the containerApps module for CORS");
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

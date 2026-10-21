using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace RetailPulse.Tests.Deployment;

/// <summary>
/// Issue #333 — Azure API Center is an opt-in module that continuously
/// synchronizes (one-way) from the Retail Pulse APIM service via a declarative
/// <c>apiSources</c> link. Source-level and compiled-ARM contracts.
/// </summary>
public sealed class ApiCenterDeploymentContractTests : IDisposable
{
    private readonly string _repoRoot = FindRepoRoot();
    private readonly string _compiledPath = Path.Combine(Path.GetTempPath(), $"retailpulse-apic-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_compiledPath))
        {
            File.Delete(_compiledPath);
        }
    }

    [Fact]
    public void Module_DeclaresApiCenterIdentityRoleAndApimSource()
    {
        string module = File.ReadAllText(Path.Combine(_repoRoot, "infra", "modules", "api-center.bicep"));

        module.Should().Contain("Microsoft.ApiCenter/services@");
        module.Should().Contain("Microsoft.ApiCenter/services/workspaces/apiSources@");
        module.Should().Contain("azureApiManagementSource");
        module.Should().Contain("var apiCenterName = 'apic-${resourceToken}'", "the name must be deterministic");
        module.Should().Contain("71522526-b88f-4d52-b57f-d31fc3546d0d", "API Management Service Reader lets API Center read APIM");
        module.Should().Contain("type: 'SystemAssigned'");
        module.Should().Contain("output apimSourceName");
    }

    [Fact]
    public void Service_DeclaresTheLiveRequiredFreeSku_WithScopedSchemaSuppression()
    {
        string module = File.ReadAllText(Path.Combine(_repoRoot, "infra", "modules", "api-center.bicep"));

        module.Should().Contain("#disable-next-line BCP187");
        module.Should().Contain("sku: {\n    name: 'Free'\n  }");
    }

    [Fact]
    public void Main_GatesApiCenterBehindDisabledDefaultAndEmitsEmptyOutputs()
    {
        string main = File.ReadAllText(Path.Combine(_repoRoot, "infra", "main.bicep"));
        string param = File.ReadAllText(Path.Combine(_repoRoot, "infra", "main.bicepparam"));

        main.Should().Contain("param apiCenterEnabled bool = false");
        main.Should().Contain("if (apiCenterEnabled)");
        main.Should().Contain("output AZURE_API_CENTER_NAME string = apiCenterEnabled ? apiCenter!.outputs.name : ''");
        param.Should().Contain("AZURE_API_CENTER_ENABLED");
        param.Should().Contain("'false'");
    }

    [Fact]
    public void CompiledArm_ProvesResourceGraphAndConditionalDeployment()
    {
        JsonElement? compiled = TryCompile();
        if (compiled is null)
        {
            return; // az/Bicep unavailable: static contracts above still apply.
        }

        JsonElement root = compiled.Value;
        JsonElement deployment = root.GetProperty("resources").GetProperty("apiCenter");
        deployment.GetProperty("condition").GetString().Should().Contain("apiCenterEnabled",
            "disabled mode must skip the whole module");
        deployment.GetProperty("dependsOn").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("apim", "the link must be ordered after APIM");

        // Nested module resources compile to an array (not a symbolic-name map).
        JsonElement[] inner = [.. deployment.GetProperty("properties").GetProperty("template")
            .GetProperty("resources").EnumerateArray()];
        string[] types = [.. inner.Select(r => r.GetProperty("type").GetString()!)];
        types.Should().Contain("Microsoft.ApiCenter/services");
        types.Should().Contain("Microsoft.Authorization/roleAssignments");

        JsonElement source = inner.Single(r => r.GetProperty("type").GetString() == "Microsoft.ApiCenter/services/workspaces/apiSources");
        source.GetRawText().Should().Contain("azureApiManagementSource");
        source.GetProperty("dependsOn").GetRawText().Should().Contain("roleAssignments",
            "the sync link must wait for the APIM reader role");

        JsonElement outputs = root.GetProperty("outputs");
        foreach (string name in new[] { "AZURE_API_CENTER_ENABLED", "AZURE_API_CENTER_NAME", "AZURE_API_CENTER_RESOURCE_ID", "AZURE_API_CENTER_APIM_SOURCE_NAME" })
        {
            outputs.TryGetProperty(name, out _).Should().BeTrue($"{name} must be emitted");
        }
    }

    [Fact]
    public void LiveVerifier_ChecksApiCenterAndApimRelationship()
    {
        string script = File.ReadAllText(Path.Combine(_repoRoot, "scripts", "Verify-ApiCenter.ps1"));

        script.Should().Contain("Microsoft.ApiCenter/services");
        script.Should().Contain("apiSources");
        script.Should().Contain("azureApiManagementSource");
        script.Should().Contain("AZURE_API_CENTER_ENABLED");
    }

    private static readonly string[] ApprovedTools = ["get_inventory", "get_stockout_risk", "get_supply_disruptions", "get_demand_forecast"];

    // Issue #336: the public API Center ARM schema/CLI cannot create kind=mcp or tool child resources;
    // APIM apiSources synchronization generates the MCP API graph, so Bicep must not duplicate it.
    private static readonly string[] ForbiddenHandAuthoredTypes =
    [
        "Microsoft.ApiCenter/services/workspaces/apis@",
        "Microsoft.ApiCenter/services/workspaces/apis/versions",
        "Microsoft.ApiCenter/services/workspaces/apis/definitions",
        "Microsoft.ApiCenter/services/workspaces/apis/deployments",
        "Microsoft.ApiCenter/services/workspaces/environments",
        "Microsoft.ApiCenter/services/metadataSchemas",
    ];

    [Fact]
    public void Module_DoesNotHandAuthorTheSynchronizedMcpGraph_AndKeepsApimSourceAuthoritative()
    {
        string module = File.ReadAllText(Path.Combine(_repoRoot, "infra", "modules", "api-center.bicep"));

        foreach (string type in ForbiddenHandAuthoredTypes)
        {
            module.Should().NotContain(type, "synchronization generates this child graph");
        }

        module.Should().NotContain("kind: 'mcp'");
        module.Should().NotContain("mcpServerApiName").And.NotContain("mcpEndpoint").And.NotContain("customProperties");
        module.Should().Contain("Microsoft.ApiCenter/services/workspaces/apiSources@");
        module.Should().Contain("azureApiManagementSource");
        module.Should().NotMatchRegex("(?i)subscription-?key|secret|apikey|password");
    }

    [Fact]
    public void Main_DoesNotWireOrOutputHandAuthoredMcpRegistration()
    {
        string main = File.ReadAllText(Path.Combine(_repoRoot, "infra", "main.bicep"));

        main.Should().NotContain("mcpServerApiName:");
        main.Should().NotContain("mcpEndpoint: apimMcpApi");
        main.Should().NotContain("AZURE_API_CENTER_MCP_API_NAME");
        main.Should().NotContain("AZURE_API_CENTER_MCP_ENVIRONMENT_NAME");
    }

    [Fact]
    public void CompiledArm_HasNoDuplicateMcpGraphAndKeepsApimSource()
    {
        JsonElement? compiled = TryCompile();
        if (compiled is null)
        {
            return;
        }

        JsonElement deployment = compiled.Value.GetProperty("resources").GetProperty("apiCenter");
        JsonElement[] inner = [.. deployment.GetProperty("properties").GetProperty("template")
            .GetProperty("resources").EnumerateArray()];
        string[] types = [.. inner.Select(r => r.GetProperty("type").GetString()!)];

        types.Should().Contain("Microsoft.ApiCenter/services/workspaces/apiSources");
        types.Should().NotContain(t => t.StartsWith("Microsoft.ApiCenter/services/workspaces/apis", StringComparison.Ordinal));
        types.Should().NotContain("Microsoft.ApiCenter/services/workspaces/environments");
        types.Should().NotContain("Microsoft.ApiCenter/services/metadataSchemas");

        JsonElement outputs = compiled.Value.GetProperty("outputs");
        outputs.TryGetProperty("AZURE_API_CENTER_MCP_API_NAME", out _).Should().BeFalse();
        outputs.TryGetProperty("AZURE_API_CENTER_MCP_ENVIRONMENT_NAME", out _).Should().BeFalse();
    }

    [Fact]
    public void LiveVerifier_DiscoversGeneratedIdsAndFailsClosedOnLinkageEndpointAndToolSet()
    {
        string script = File.ReadAllText(Path.Combine(_repoRoot, "scripts", "Verify-ApiCenter.ps1"));

        script.Should().Contain("Test-McpSync");
        script.Should().Contain("workspaces/default/apis");
        script.Should().Contain("properties.kind -eq 'mcp'");
        script.Should().Contain("/deployments").And.Contain("/versions").And.Contain("/definitions");
        script.Should().Contain("environmentId").And.Contain("definitionId").And.Contain("runtimeUri");
        script.Should().Contain("properties.displayName").And.Contain("operationId")
            .And.Contain("resolvedOperation").And.Contain("expected GET");
        script.Should().Contain("state -eq 'active'").And.Contain("does not belong to the synchronized API version");
        script.Should().Contain("linkState").And.Contain("apiSource linkState");
        script.Should().Contain("ARM GET forbidden").And.Contain("authorization failure as pending");
        script.Should().Contain("lifecycleStage -ne 'production'");
        script.Should().Contain("Azure API Management");
        script.Should().Contain("2025-09-01-preview", "APIM MCP tools are read from supported APIM ARM tool resources");
        script.Should().Contain("[switch]$SelfTest");
        script.Should().Contain("MaxAttempts").And.Contain("exit 3").And.Contain("24 hours");
        script.Should().Contain("does not construct it");
        script.Should().NotContain("workspaces/default/apis/$McpApiName", "the API Center API id is generated, not the APIM API id");
        script.Should().NotContain("azure-apicenter.ms", "never construct the registry endpoint from memory");
        foreach (string tool in ApprovedTools)
        {
            script.Should().Contain(tool);
        }

        script.Should().NotMatchRegex("(?i)-Method\\s+(Put|Post|Patch|Delete)|az\\s+(apic|rest)\\s+.*(create|delete|put)", "the verifier is read-only");
    }

    [Fact]
    public void LiveVerifier_SelfTestProvesDiscoveryPendingAndFailClosedBehavior()
    {
        string script = File.ReadAllText(Path.Combine(_repoRoot, "scripts", "Verify-ApiCenter.ps1"));
        foreach (string probe in new[]
        {
            "generated-ID synchronized graph passes", "resolved generated API id, not the APIM API id",
            "unrelated MCP deployment does not fail a missing target", "missing versions is pending",
            "partial definition sync is pending", "non-production lifecycle fails", "non-APIM environment fails",
            "endpoint mismatch fails", "case-changed endpoint path fails", "missing endpoint fails closed", "extra APIM tool fails",
            "missing APIM tool fails", "renamed APIM tool fails", "case-changed APIM tool fails",
            "write-bound APIM tool fails", "case-changed operation path fails",
            "inactive deployment fails", "missing definition linkage fails", "apiSource error fails",
            "apiSource syncing is pending", "no apiSources fails",
        })
        {
            script.Should().Contain(probe);
        }
    }

    private JsonElement? TryCompile()
    {
        string main = Path.Combine(_repoRoot, "infra", "main.bicep");
        if (!Run(["bicep", "install"], out _) || !Run(["bicep", "build", "--file", main, "--outfile", _compiledPath], out string output))
        {
            return null;
        }

        output.Should().NotContain("Error ", "main.bicep must compile");
        return JsonDocument.Parse(File.ReadAllText(_compiledPath)).RootElement.Clone();
    }

    private static bool Run(string[] args, out string output)
    {
        output = string.Empty;
        try
        {
            ProcessStartInfo info = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "az" } }
                : new ProcessStartInfo("az");
            foreach (string a in args)
            {
                info.ArgumentList.Add(a);
            }

            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.UseShellExecute = false;
            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            string stderr = process.StandardError.ReadToEnd();
            output = process.StandardOutput.ReadToEnd() + stderr;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "RetailPulse.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Repository root not found.");
    }
}

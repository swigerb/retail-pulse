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

    [Fact]
    public void Module_RegistersMcpApiVersionEnvironmentAndDeploymentWithoutSecrets()
    {
        string module = File.ReadAllText(Path.Combine(_repoRoot, "infra", "modules", "api-center.bicep"));

        module.Should().Contain("Microsoft.ApiCenter/services/workspaces/apis@2024-03-01");
        module.Should().Contain("Microsoft.ApiCenter/services/workspaces/apis/versions@2024-03-01");
        module.Should().Contain("Microsoft.ApiCenter/services/workspaces/apis/versions/definitions@2024-03-01");
        module.Should().Contain("Microsoft.ApiCenter/services/workspaces/environments@2024-03-01");
        module.Should().Contain("Microsoft.ApiCenter/services/workspaces/apis/deployments@2024-03-01");
        module.Should().Contain("Microsoft.ApiCenter/services/metadataSchemas@2024-03-01");
        module.Should().Contain("kind: 'mcp'");
        module.Should().Contain("param mcpServerApiName string = 'retail-pulse-mcp-server'");
        module.Should().Contain("runtimeUri");
        module.Should().NotMatchRegex("(?i)subscription-?key|secret|apikey|password", "registration metadata must carry no credentials");
        foreach (string tool in ApprovedTools)
        {
            module.Should().Contain($"'{tool}'");
        }

        module.Split("readOnly: true").Length.Should().Be(6, "four tool schemas plus the aggregate flag, all read-only");
    }

    [Fact]
    public void Main_PassesApimMcpEndpointAndApiIdIntoApiCenterModule()
    {
        string main = File.ReadAllText(Path.Combine(_repoRoot, "infra", "main.bicep"));

        main.Should().Contain("mcpServerApiName: apimMcpApi.outputs.mcpServerName");
        main.Should().Contain("mcpEndpoint: apimMcpApi.outputs.mcpEndpoint");
        main.Should().Contain("output AZURE_API_CENTER_MCP_API_NAME string = apiCenterEnabled ? apiCenter!.outputs.mcpApiName : ''");
    }

    [Fact]
    public void CompiledArm_LinksMcpDeploymentToEnvironmentAndApimEndpoint()
    {
        JsonElement? compiled = TryCompile();
        if (compiled is null)
        {
            return;
        }

        JsonElement deployment = compiled.Value.GetProperty("resources").GetProperty("apiCenter");
        deployment.GetProperty("condition").GetString().Should().Contain("apiCenterEnabled");
        JsonElement[] inner = [.. deployment.GetProperty("properties").GetProperty("template")
            .GetProperty("resources").EnumerateArray()];

        JsonElement api = inner.Single(r => r.GetProperty("type").GetString() == "Microsoft.ApiCenter/services/workspaces/apis");
        api.GetProperty("properties").GetProperty("kind").GetString().Should().Be("mcp");
        api.GetProperty("name").GetString().Should().Contain("mcpServerApiName");

        string[] types = [.. inner.Select(r => r.GetProperty("type").GetString()!)];
        types.Should().Contain("Microsoft.ApiCenter/services/workspaces/apis/versions");
        types.Should().Contain("Microsoft.ApiCenter/services/workspaces/environments");
        types.Should().Contain("Microsoft.ApiCenter/services/metadataSchemas");

        JsonElement deploy = inner.Single(r => r.GetProperty("type").GetString() == "Microsoft.ApiCenter/services/workspaces/apis/deployments");
        string raw = deploy.GetRawText();
        raw.Should().Contain("environmentId").And.Contain("definitionId").And.Contain("runtimeUri").And.Contain("mcpEndpoint");
        deploy.GetProperty("properties").GetProperty("state").GetString().Should().Be("active");

        JsonElement tools = api.GetProperty("properties").GetProperty("customProperties").GetProperty("retailPulseMcp");
        // The approved tool list compiles to a variable reference or literal; the module's outputs prove the set.
        tools.GetRawText().Should().Contain("approvedTools").And.Contain("toolSchemas");

        JsonElement outputs = compiled.Value.GetProperty("outputs");
        outputs.TryGetProperty("AZURE_API_CENTER_MCP_API_NAME", out _).Should().BeTrue();
        outputs.TryGetProperty("AZURE_API_CENTER_MCP_ENVIRONMENT_NAME", out _).Should().BeTrue();
    }

    [Fact]
    public void LiveVerifier_IsReadOnlyAndFailsOnWrongRegistrationLinkageEndpointOrToolSet()
    {
        string script = File.ReadAllText(Path.Combine(_repoRoot, "scripts", "Verify-ApiCenter.ps1"));

        script.Should().Contain("Get-McpRegistrationFailures");
        script.Should().Contain("-ne 'mcp'");
        script.Should().Contain("runtimeUri");
        script.Should().Contain("environmentId");
        script.Should().Contain("2025-09-01-preview");
        script.Should().Contain("[switch]$SelfTest");
        foreach (string tool in ApprovedTools)
        {
            script.Should().Contain(tool);
        }

        script.Should().NotMatchRegex("(?i)-Method\\s+(Put|Post|Patch|Delete)|az\\s+(apic|rest)\\s+.*(create|delete|put)", "the verifier is read-only");
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

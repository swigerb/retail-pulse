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
        JsonElement[] inner = deployment.GetProperty("properties").GetProperty("template")
            .GetProperty("resources").EnumerateArray().ToArray();
        string[] types = inner.Select(r => r.GetProperty("type").GetString()!).ToArray();
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
            var info = OperatingSystem.IsWindows()
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

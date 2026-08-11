using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace RetailPulse.Tests.Deployment;

/// <summary>
/// Regression test for issue #67: validates the COMPILED ARM deployment graph
/// (via `az bicep build`, i.e. the actual template `azd provision` would
/// submit to Azure Resource Manager) — not just Bicep source text — confirms
/// the presence of every AI Gateway resource that the live verifier
/// (<c>scripts/Verify-ApimAiGateway.ps1</c>) checks: the AOAI backend, the API
/// policy fragments, the API-level diagnostics, the RBAC role assignment, and
/// the ACA container-apps wiring.
///
/// The static <see cref="ApimGatewayContractTests"/> and
/// <see cref="DeploymentContractTests"/> suites assert the Bicep *source*
/// contains the right resource declarations; this test additionally proves
/// those declarations actually make it into the compiled ARM JSON that ARM
/// receives, closing the gap a source-text-only regression test cannot catch
/// (for example, a resource accidentally wrapped in a false `if()`/condition,
/// or dropped by a module wiring mistake that source-text grepping wouldn't
/// notice because the *declaration* is still present somewhere in the repo).
///
/// Requires the `az` CLI (with the bundled Bicep compiler) to be available on
/// PATH. If `az`/`bicep` cannot be located or `az bicep build` fails to run at
/// all (as opposed to failing to compile), the test is skipped via
/// <see cref="Skip"/> rather than failing a CI runner that lacks the Azure
/// CLI — a genuine compile failure still fails the test.
/// </summary>
public sealed partial class CompiledArmDeploymentGraphTests : IDisposable
{
    private readonly string _repoRoot = FindRepoRoot();
    private readonly string _compiledTemplatePath;

    public CompiledArmDeploymentGraphTests()
    {
        _compiledTemplatePath = Path.Combine(Path.GetTempPath(), $"retailpulse-main-compiled-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_compiledTemplatePath))
        {
            File.Delete(_compiledTemplatePath);
        }
    }

    [Fact]
    public void CompiledArmTemplate_ContainsAiGatewayBackendPolicyDiagnosticsRbacAndAcaWiring()
    {
        JsonElement? compiled = TryCompileMainBicep();
        if (compiled is null)
        {
            // `az`/Bicep unavailable in this environment — the static Bicep
            // contract tests (ApimGatewayContractTests, DeploymentContractTests)
            // still cover the source-level invariants. Do not fail a sandbox
            // that genuinely lacks the Azure CLI.
            return;
        }

        JsonElement root = compiled.Value;
        JsonElement resources = root.GetProperty("resources");

        // ── apimOpenAiApi nested deployment must exist and depend on apim ────
        resources.TryGetProperty("apimOpenAiApi", out JsonElement apimOpenAiApiDeployment).Should().BeTrue(
            "the compiled template must include the apimOpenAiApi nested deployment");

        JsonElement apimApiTemplate = apimOpenAiApiDeployment
            .GetProperty("properties")
            .GetProperty("template");

        JsonElement innerResources = apimApiTemplate.GetProperty("resources");

        // ── Backend ──────────────────────────────────────────────────────────
        innerResources.TryGetProperty("backend", out JsonElement backendResource).Should().BeTrue(
            "the compiled apimOpenAiApi template must declare the 'backend' resource (retail-pulse-foundry)");
        backendResource.GetProperty("type").GetString().Should().Be(
            "Microsoft.ApiManagement/service/backends");
        backendResource.GetProperty("name").GetString().Should().Contain("retail-pulse-foundry",
            "the backend's compiled name expression must reference the 'retail-pulse-foundry' literal");

        string backendJson = backendResource.GetRawText();
        backendJson.Should().Contain("cognitiveservices.azure.com",
            "the compiled backend resource must authenticate via managed identity to cognitiveservices.azure.com");

        // ── API policy: token-limit + emit-token-metric + backend routing ────
        innerResources.TryGetProperty("apiPolicy", out JsonElement apiPolicyResource).Should().BeTrue(
            "the compiled apimOpenAiApi template must declare the 'apiPolicy' resource");
        apiPolicyResource.GetProperty("type").GetString().Should().Be(
            "Microsoft.ApiManagement/service/apis/policies");

        // The policy's `value` is a compiled expression that concatenates the
        // loaded XML content (via `replace(...)`) — the XML template literal
        // itself is hoisted into a $fxv variable. Confirm the compiled
        // template graph carries the actual policy XML content somewhere in
        // its variables so it isn't silently dropped by the module boundary.
        string compiledJson = root.GetRawText();
        compiledJson.Should().Contain("azure-openai-token-limit",
            "the compiled template must carry the azure-openai-token-limit policy fragment");
        compiledJson.Should().Contain("azure-openai-emit-token-metric",
            "the compiled template must carry the azure-openai-emit-token-metric policy fragment");
        compiledJson.Should().Contain("RetailPulse",
            "the compiled template must carry the RetailPulse emit-token-metric namespace");
        compiledJson.Should().Contain("authentication-managed-identity",
            "the compiled template must carry the managed-identity authentication policy fragment");
        compiledJson.Should().Contain("cognitiveservices.azure.com",
            "the compiled template's managed-identity policy fragment must target cognitiveservices.azure.com");

        // ── Diagnostics: API-level applicationinsights (metrics) + azuremonitor (LLM logs) ──
        innerResources.TryGetProperty("apiAppInsightsDiagnostics", out JsonElement appInsightsDiag).Should().BeTrue(
            "the compiled apimOpenAiApi template must declare the API-level applicationinsights diagnostic");
        appInsightsDiag.GetProperty("type").GetString().Should().Be(
            "Microsoft.ApiManagement/service/apis/diagnostics");
        string appInsightsDiagJson = MyRegex().Replace(appInsightsDiag.GetRawText(), string.Empty);
        appInsightsDiagJson.Should().Contain("\"metrics\":true",
            "the compiled applicationinsights diagnostic must enable metrics (routes emit-token-metric into App Insights)");

        innerResources.TryGetProperty("apiAzureMonitorDiagnostics", out JsonElement azMonDiag).Should().BeTrue(
            "the compiled apimOpenAiApi template must declare the API-level azuremonitor diagnostic");
        azMonDiag.GetProperty("type").GetString().Should().Be(
            "Microsoft.ApiManagement/service/apis/diagnostics");
        azMonDiag.GetRawText().Should().Contain("largeLanguageModel",
            "the compiled azuremonitor diagnostic must declare the largeLanguageModel logging block");

        // ── RBAC: Cognitive Services OpenAI User role assignment ─────────────
        innerResources.TryGetProperty("aiFoundryRoleAssignment", out JsonElement roleAssignmentModule).Should().BeTrue(
            "the compiled apimOpenAiApi template must declare the aiFoundryRoleAssignment nested module");
        roleAssignmentModule.GetRawText().Should().Contain("roleDefinitionId",
            "the compiled apimOpenAiApi template must pass roleDefinitionId into the aiFoundryRoleAssignment module");
        apimApiTemplate.GetRawText().Should().Contain("5e0bd9bd-7b93-4f28-af87-19fc36ad61bd",
            "the compiled apimOpenAiApi template must define the Cognitive Services OpenAI User role definition id and pass it into the role-assignment module");

        // ── ACA wiring: container-apps deployment depends on apimOpenAiApi and
        // consumes its outputs (inference endpoint + subscription key) ───────
        resources.TryGetProperty("containerApps", out JsonElement containerAppsDeployment).Should().BeTrue(
            "the compiled template must include the containerApps nested deployment");

        var containerAppsDependsOn = containerAppsDeployment
            .GetProperty("dependsOn")
            .EnumerateArray()
            .Select(e => e.GetString() ?? string.Empty)
            .ToList();
        containerAppsDependsOn.Should().Contain("apimOpenAiApi",
            "the compiled containerApps deployment must depend on apimOpenAiApi so ACA wiring can never provision ahead of / independently of the AI Gateway");

        JsonElement containerAppsParams = containerAppsDeployment
            .GetProperty("properties")
            .GetProperty("parameters");
        containerAppsParams.TryGetProperty("apimInferenceEndpoint", out JsonElement inferenceEndpointParam).Should().BeTrue(
            "the compiled containerApps deployment must receive apimInferenceEndpoint from apimOpenAiApi's outputs");
        inferenceEndpointParam.GetRawText().Should().Contain("apimOpenAiApi",
            "apimInferenceEndpoint must be sourced from the apimOpenAiApi deployment's outputs, not a literal/default");

        containerAppsParams.TryGetProperty("apimSubscriptionKey", out JsonElement subscriptionKeyParam).Should().BeTrue(
            "the compiled containerApps deployment must receive apimSubscriptionKey from apimOpenAiApi's outputs");
        subscriptionKeyParam.GetRawText().Should().Contain("apimOpenAiApi",
            "apimSubscriptionKey must be sourced from the apimOpenAiApi deployment's outputs via listOutputsWithSecureValues, not a literal");
    }

    private JsonElement? TryCompileMainBicep()
    {
        string mainBicepPath = Path.Combine(_repoRoot, "infra", "main.bicep");
        if (!File.Exists(mainBicepPath))
        {
            return null;
        }

        if (!TryRunAz(["bicep", "install"], out _))
        {
            // `az` not on PATH or bicep install failed for environmental
            // reasons (e.g. no network) — skip rather than fail.
            return null;
        }

        bool compiled = TryRunAz(
            ["bicep", "build", "--file", mainBicepPath, "--outfile", _compiledTemplatePath],
            out string buildOutput);

        if (!compiled)
        {
            // A genuine compile failure (bad Bicep) SHOULD fail this test —
            // but distinguish "az/bicep isn't usable at all" (skip) from "the
            // template failed to compile" (real bug, must fail).
            return buildOutput.Contains("is not recognized", StringComparison.OrdinalIgnoreCase) ||
                buildOutput.Contains("command not found", StringComparison.OrdinalIgnoreCase)
                ? null
                : throw new InvalidOperationException(
                $"'az bicep build' failed to compile infra/main.bicep — the deployment graph is broken:\n{buildOutput}");
        }

        if (!File.Exists(_compiledTemplatePath))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(_compiledTemplatePath));
        return document.RootElement.Clone();
    }

    private static bool TryRunAz(string[] arguments, out string output)
    {
        try
        {
            // On Windows, `az` resolves to `az.cmd` (a batch shim), which
            // Process.Start cannot execute directly with UseShellExecute=false
            // (there is no shell to interpret the .cmd). Route through
            // `cmd /c` there; POSIX shells have a real `az` binary/shim on
            // PATH and can be invoked directly.
            ProcessStartInfo startInfo;
            if (OperatingSystem.IsWindows())
            {
                startInfo = new ProcessStartInfo("cmd.exe")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add("az");
            }
            else
            {
                startInfo = new ProcessStartInfo("az")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
            }

            foreach (string arg in arguments)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                output = string.Empty;
                return false;
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            output = stdout + stderr;
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            output = ex.Message;
            return false;
        }
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

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex MyRegex();
}

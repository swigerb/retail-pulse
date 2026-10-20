using System.Diagnostics;
using System.Text.RegularExpressions;
using FluentAssertions;
using RetailPulse.Api.OpenAI.Gateway;
using RetailPulse.Api.Packs;
using RetailPulse.Contracts.Gateway;
using RetailPulse.Tests.Packs;
using Xunit.Sdk;

namespace RetailPulse.Tests.Deployment;

/// <summary>
/// Offline integrated acceptance for the retailer AI Gateway (#340). It composes the merged probes, policy fragments,
/// evidence parser and pack loader; it never re-implements policy behaviour and never skips. Every failure names the
/// missing invariant. Live deployment evidence is owned by #341 / #342, not this suite.
/// </summary>
public sealed class IntegratedGatewayAcceptanceTests
{
    private static readonly string Root = PackTestPaths.RepoRoot;

    private static readonly string[] _packs = ["kroger-retail", "walgreens-retail", "staples-retail", "lowes-retail"];

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    private static void Invariant(string id, bool holds, string detail)
    {
        if (!holds)
        {
            throw new XunitException($"MISSING INVARIANT [{id}]: {detail}");
        }
    }

    private static void Contains(string id, string text, string expected, string where) =>
        Invariant(id, text.Contains(expected, StringComparison.Ordinal), $"'{expected}' not found in {where}");

    private static string RunProbeSelfTest(string id, string script, params string[] expectedMarkers)
    {
        string path = Path.Combine(Root, "scripts", script);
        Invariant(id, File.Exists(path), $"probe scripts/{script} is missing");
        var psi = new ProcessStartInfo("node", $"\"{path}\" --self-test")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        string output;
        int exit;
        try
        {
            using Process p = Process.Start(psi)!;
            Task<string> err = p.StandardError.ReadToEndAsync();
            output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(90_000).Should().BeTrue($"{script} --self-test timed out");
            exit = p.ExitCode;
            output += err.Result;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new XunitException($"MISSING INVARIANT [{id}]: node is required to run scripts/{script} --self-test ({ex.Message})");
        }

        Invariant(id, exit == 0, $"scripts/{script} --self-test exited {exit}: {output}");
        foreach (string marker in expectedMarkers)
        {
            Contains(id, output, marker, $"scripts/{script} --self-test output");
        }

        return output;
    }

    [Theory]
    [InlineData("kroger-retail")]
    [InlineData("walgreens-retail")]
    [InlineData("staples-retail")]
    [InlineData("lowes-retail")]
    public void Packs_LoadWithGatewayScenarioAndSyntheticDisclosure(string packName)
    {
        LoadedPack pack = default!;
        try
        {
            pack = PackLoader.ForDirectory(PackTestPaths.PacksRoot).Load(packName);
        }
        catch (Exception ex) when (ex is not XunitException)
        {
            throw new XunitException($"MISSING INVARIANT [pack-loading]: {packName} does not load: {ex.Message}");
        }

        Invariant("pack-loading", pack.Metadata.Key == packName, $"{packName} metadata key is '{pack.Metadata.Key}'");
        Invariant("pack-loading", pack.Agents.Agents.ContainsKey("router"), $"{packName} has no router agent");
        Invariant("pack-loading", pack.StartingTasks.SelectMany(c => c.Tasks).Any(), $"{packName} has no starting tasks");
        string attribution = pack.Metadata.Attribution;
        Invariant("pack-synthetic-disclosure",
            attribution.Contains("synthetic", StringComparison.OrdinalIgnoreCase)
            && attribution.Contains("unofficial", StringComparison.OrdinalIgnoreCase),
            $"{packName} attribution must state unofficial + synthetic");
    }

    [Fact]
    public void Packs_CarryTheirRetailerGatewayScenarioPrompt()
    {
        var expected = new Dictionary<string, string>
        {
            ["kroger-retail"] = "Cache-bypass decision",
            ["walgreens-retail"] = "Gateway governance check",
            ["staples-retail"] = "AI gateway governance check",
            ["lowes-retail"] = "Governed supplier lookup",
        };
        foreach ((string packName, string task) in expected)
        {
            LoadedPack pack = PackLoader.ForDirectory(PackTestPaths.PacksRoot).Load(packName);
            Invariant("pack-gateway-scenario",
                pack.StartingTasks.SelectMany(c => c.Tasks).Any(t => t.Name == task),
                $"{packName} lacks the starting task '{task}' used by the walkthrough");
        }
    }

    [Fact]
    public void ControlRoom_NormalEvidence_ParsesFromTheRealGatewayHeaders()
    {
        var headers = new Dictionary<string, string>
        {
            [GatewayEvidenceHeaders.RequestId] = "req-340",
            [GatewayEvidenceHeaders.Backend] = "primary-backend",
            [GatewayEvidenceHeaders.Route] = "primary",
            [GatewayEvidenceHeaders.TokensConsumed] = "120",
            [GatewayEvidenceHeaders.TokensRemaining] = "79880",
            [GatewayEvidenceHeaders.Quota] = "allowed",
            [GatewayEvidenceHeaders.Cache] = "miss",
            [GatewayEvidenceHeaders.Safety] = "passed",
            [GatewayEvidenceHeaders.PolicyVersion] = "v1",
            [GatewayEvidenceHeaders.LatencyMs] = "850",
            [GatewayEvidenceHeaders.BackendLatencyMs] = "800",
        };

        GatewayCallEvidence e = GatewayEvidenceHeaders.Parse(n => headers.GetValueOrDefault(n), 200);

        Invariant("control-room-normal-evidence", e.Status == GatewayEvidenceStatus.Observed && !e.IsEmpty, "normal headers did not yield observed evidence");
        Invariant("control-room-normal-evidence", e.Route == GatewayRouteState.Primary, $"route was {e.Route}, expected Primary");
        Invariant("control-room-normal-evidence", e.Quota == GatewayOutcome.Allowed && e.Safety == GatewayOutcome.Passed && e.Cache == GatewayOutcome.Miss,
            $"quota/safety/cache were {e.Quota}/{e.Safety}/{e.Cache}");
        Invariant("control-room-normal-evidence", e.TokensConsumed == 120 && e.TokensRemaining == 79880, "token counts were not mapped");
        Invariant("control-room-normal-evidence", e.FailoverDemo is null, "normal traffic must not carry failover demo evidence");
        Invariant("control-room-normal-evidence", GatewayEvidenceHeaders.Parse(_ => null, 200).IsEmpty,
            "missing headers must parse to explicit unknown evidence, not a fabricated success");

        string ribbon = Read("src", "RetailPulse.Web", "src", "components", "GatewayControlRoom.tsx");
        Contains("control-room-normal-evidence", ribbon, "Quota reset walkthrough", "GatewayControlRoom.tsx");
    }

    [Fact]
    public void AuthorizedFailover_ProbeProvesRealPrimaryFailureAndSpoofRejection()
    {
        RunProbeSelfTest("authorized-failover", "probe-failover-demo.cjs",
            "[enabled:correct]", "[enabled:spoof]", "[enabled:no-failover]", "[enabled:open]", "[disabled:correct]");

        var headers = new Dictionary<string, string>
        {
            [GatewayEvidenceHeaders.Route] = "failover",
            [GatewayEvidenceHeaders.FailoverDemo] = "active",
            [GatewayEvidenceHeaders.PrimaryStatus] = "400",
            [GatewayEvidenceHeaders.Attempts] = "2",
            [GatewayEvidenceHeaders.Fallback] = "used",
            [GatewayEvidenceHeaders.Backend] = "secondary-backend",
        };
        GatewayCallEvidence e = GatewayEvidenceHeaders.Parse(n => headers.GetValueOrDefault(n), 200);
        Invariant("authorized-failover", e.Route == GatewayRouteState.Failover, "Control Room parser did not report the failover route");
        Invariant("authorized-failover", e.FailoverDemo is { PrimaryStatusCode: 400, Attempts: 2, FallbackUsed: true },
            $"failover demo evidence not mapped: {e.FailoverDemo}");
    }

    [Fact]
    public void PreModelSafetyBlock_ProbeProvesBlockBeforeBackendAndFailClosedRegulated()
    {
        RunProbeSelfTest("pre-model-safety-block", "probe-gateway-safety.cjs",
            "[ok]   attack:correct", "[ok]   attack:backend-was-invoked", "[ok]   fault-503:regulated-fail-closed", "[ok]   outbound:correct");

        string policy = Read("infra", "modules", "apim-openai-policy.xml");
        int safety = policy.IndexOf("{safety-inbound}", StringComparison.Ordinal);
        int backend = policy.IndexOf("<set-backend-service", StringComparison.Ordinal);
        Invariant("pre-model-safety-block", safety >= 0 && backend >= 0 && safety < backend,
            "safety-inbound must be rendered before backend selection in apim-openai-policy.xml");
    }

    [Fact]
    public void TokenQuotaAndReset_ProbeProves429RetryAfterAndGenerationReset()
    {
        RunProbeSelfTest("token-quota-reset", "probe-token-budget.cjs",
            "scenario correct: expected pass", "scenario no-reset: expected failure", "scenario bad-retry: expected failure", "SELFTEST PASS");

        string docs = Read("docs", "token-budgets.md");
        Contains("token-quota-reset", docs, "rp-budget-demo-armed", "docs/token-budgets.md");
        Contains("token-quota-reset", docs, "rp-budget-demo-generation", "docs/token-budgets.md");
    }

    [Fact]
    public void ApiCenterAndMcpDiscovery_VerifierAndGovernedToolSurfaceExist()
    {
        string verifier = Read("scripts", "Verify-ApiCenter.ps1");
        foreach (string probe in new[] { "generated-ID synchronized graph passes", "missing endpoint fails closed", "extra APIM tool fails", "write-bound APIM tool fails" })
        {
            Contains("api-center-mcp-discovery", verifier, probe, "scripts/Verify-ApiCenter.ps1 self-test");
        }

        string mcp = Read("infra", "modules", "apim-mcp-api.bicep");
        Contains("api-center-mcp-discovery", mcp, "subscriptionRequired", "infra/modules/apim-mcp-api.bicep");
        Invariant("api-center-mcp-discovery", File.Exists(Path.Combine(Root, "infra", "modules", "api-center.bicep")), "infra/modules/api-center.bicep is missing");
    }

    [Fact]
    public void RegulatedMode_IsMetadataOnlyAndAlwaysBypassesCache()
    {
        string api = Read("infra", "modules", "apim-openai-api.bicep");
        Contains("regulated-metadata-only", api, "var bodiesDisabled = gatewaySafetyEnabled || regulatedRetailMode", "apim-openai-api.bicep");
        Contains("regulated-metadata-only", api, "bytes: bodiesDisabled ? 0 : 8192", "apim-openai-api.bicep");
        Contains("regulated-metadata-only", api, "logs: bodiesDisabled ? 'disabled' : 'enabled'", "apim-openai-api.bicep");
        Contains("regulated-cache-bypass", api, "'{cache-regulated}', string(regulatedRetailMode)", "apim-openai-api.bicep");
        Contains("regulated-cache-bypass", Read("infra", "modules", "apim-openai-semantic-cache-inbound.xml"), "return &quot;regulated&quot;", "semantic-cache inbound fragment");
        Contains("regulated-fail-closed", api, "regulatedRetailMode ? 'regulated' : gatewaySafetyMode", "apim-openai-api.bicep");

        var headers = new Dictionary<string, string> { [GatewayEvidenceHeaders.Cache] = "bypass", [GatewayEvidenceHeaders.CacheReason] = "regulated" };
        GatewayCallEvidence e = GatewayEvidenceHeaders.Parse(n => headers.GetValueOrDefault(n), 200);
        Invariant("regulated-cache-bypass", e.Cache == GatewayOutcome.Bypass, $"regulated bypass parsed as {e.Cache}");
    }

    [Fact]
    public void SemanticCache_ProbeProvesHitWithoutBackendAndClosedVocabularyBypass()
    {
        RunProbeSelfTest("semantic-cache-hit-bypass", "probe-semantic-cache.cjs",
            "PASS self-test: conforming gateway passes", "PASS self-test: hit that contacts backend fails",
            "PASS self-test: caller-header partition fails", "PASS self-test: cache-unavailable fall-through passes");

        var hit = new Dictionary<string, string> { [GatewayEvidenceHeaders.Cache] = "hit" };
        Invariant("semantic-cache-hit-bypass", GatewayEvidenceHeaders.Parse(n => hit.GetValueOrDefault(n), 200).Cache == GatewayOutcome.Hit,
            "Control Room parser does not report a cache hit");
    }

    [Fact]
    public void Walkthrough_DocumentsSevenPrioritiesFourPacksAndExactSetupResetCommands()
    {
        string doc = Read("docs", "demo-walkthrough.md");
        Invariant("walkthrough-section", doc.Contains("## Retailer AI Gateway Walkthrough", StringComparison.Ordinal), "docs/demo-walkthrough.md has no 'Retailer AI Gateway Walkthrough' section");
        string section = doc[doc.IndexOf("## Retailer AI Gateway Walkthrough", StringComparison.Ordinal)..];
        int next = section.IndexOf("\n## ", 5, StringComparison.Ordinal);
        if (next > 0)
        {
            section = section[..next];
        }

        foreach (string pack in _packs)
        {
            Contains("walkthrough-packs", section, $"`{pack}`", "gateway walkthrough");
        }

        foreach (string priority in new[]
        {
            "Control Room evidence", "Authorized failover", "Pre-model safety block", "Token quota and reset",
            "API Center and MCP discovery", "Regulated-retail mode", "Semantic cache",
        })
        {
            Contains("walkthrough-priorities", section, priority, "gateway walkthrough");
        }

        foreach (string command in new[]
        {
            "az apim nv update --service-name <apim> -g <rg> --named-value-id rp-failover-demo-armed --value true",
            "az apim nv update --service-name <apim> -g <rg> --named-value-id rp-failover-demo-armed --value false",
            "az apim nv update -g <rg> -n <apim> --named-value-id rp-budget-demo-armed --value true",
            "az apim nv update -g <rg> -n <apim> --named-value-id rp-budget-demo-generation --value <new number>",
            "az apim nv update -g <rg> -n <apim> --named-value-id rp-budget-demo-armed --value false",
            "azd env set AZURE_APIM_FAILOVER_DEMO_ENABLED true",
            "azd env set AZURE_APIM_TOKEN_BUDGET_DEMO_ENABLED true",
            "azd env set AZURE_APIM_GATEWAY_SAFETY_ENABLED true",
            "azd env set AZURE_SEMANTIC_CACHE_ENABLED true",
            "azd env set AZURE_APIM_SEMANTIC_CACHE_POLICY_ENABLED true",
            "azd env set AZURE_API_CENTER_ENABLED true",
            "azd env set AZURE_REGULATED_RETAIL_MODE true",
            "azd env set AZURE_REGULATED_RETAIL_MODE false",
            "Packs__Active=",
            "node scripts/probe-failover-demo.cjs",
            "node scripts/probe-gateway-safety.cjs",
            "node scripts/probe-token-budget.cjs",
            "node scripts/probe-semantic-cache.cjs",
            "./scripts/Verify-ApiCenter.ps1",
        })
        {
            Contains("walkthrough-commands", section, command, "gateway walkthrough");
        }

        Contains("walkthrough-live-ownership", section, "#341", "gateway walkthrough");
        Contains("walkthrough-live-ownership", section, "#342", "gateway walkthrough");
        Invariant("walkthrough-claims", Regex.IsMatch(section, "integrated Azure operating model", RegexOptions.IgnoreCase), "claim must be framed as an integrated Azure operating model");
        Invariant("walkthrough-claims", Regex.IsMatch(section, "not exclusive", RegexOptions.IgnoreCase), "walkthrough must say the capabilities are not exclusive features");
        Invariant("walkthrough-claims", !Regex.IsMatch(section, @"\b(only|unique(ly)?|exclusive(ly)?)\s+(to\s+)?(Azure|APIM)\b|\bnobody else\b|\bno other (vendor|platform)\b", RegexOptions.IgnoreCase),
            "walkthrough makes an exclusivity claim");
        Invariant("walkthrough-no-real-data", !Regex.IsMatch(section, @"\b(sk-[A-Za-z0-9]{16,}|\d{3}-\d{2}-\d{4})\b"), "walkthrough contains a secret or identifier-like value");
    }
}

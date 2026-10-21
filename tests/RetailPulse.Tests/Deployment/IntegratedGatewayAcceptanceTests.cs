using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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
public sealed partial class IntegratedGatewayAcceptanceTests
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

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(90);

    private static async Task<(int Exit, string Output)> RunProcessAsync(string id, string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Root,
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        Process p;
        try
        {
            p = Process.Start(psi)!;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new XunitException($"MISSING INVARIANT [{id}]: '{tool}' is required on PATH to run {string.Join(' ', args)} ({ex.Message})");
        }

        using (p)
        {
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(ProcessTimeout);
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                p.Kill(entireProcessTree: true);
                await p.WaitForExitAsync();
                throw new XunitException($"MISSING INVARIANT [{id}]: '{tool} {string.Join(' ', args)}' did not finish within {ProcessTimeout.TotalSeconds:0}s and was killed");
            }

            return (p.ExitCode, await stdout + await stderr);
        }
    }

    private static async Task<string> RunProbeSelfTestAsync(string id, string script, params string[] expectedMarkers)
    {
        string path = Path.Combine(Root, "scripts", script);
        Invariant(id, File.Exists(path), $"probe scripts/{script} is missing");
        (int exit, string output) = await RunProcessAsync(id, "node", path, "--self-test");
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
    public async Task AuthorizedFailover_ProbeProvesRealPrimaryFailureAndSpoofRejection()
    {
        await RunProbeSelfTestAsync("authorized-failover", "probe-failover-demo.cjs",
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
    public async Task PreModelSafetyBlock_ProbeProvesBlockBeforeBackendAndFailClosedRegulated()
    {
        await RunProbeSelfTestAsync("pre-model-safety-block", "probe-gateway-safety.cjs",
            "[ok]   attack:correct", "[ok]   attack:backend-was-invoked", "[ok]   fault-503:regulated-fail-closed", "[ok]   outbound:correct");

        string policy = Read("infra", "modules", "apim-openai-policy.xml");
        int safety = policy.IndexOf("{safety-inbound}", StringComparison.Ordinal);
        int backend = policy.IndexOf("<set-backend-service", StringComparison.Ordinal);
        Invariant("pre-model-safety-block", safety >= 0 && backend >= 0 && safety < backend,
            "safety-inbound must be rendered before backend selection in apim-openai-policy.xml");
    }

    [Fact]
    public async Task TokenQuotaAndReset_ProbeProves429RetryAfterAndGenerationReset()
    {
        await RunProbeSelfTestAsync("token-quota-reset", "probe-token-budget.cjs",
            "scenario correct: expected pass", "scenario no-reset: expected failure", "scenario bad-retry: expected failure", "SELFTEST PASS");

        string docs = Read("docs", "token-budgets.md");
        Contains("token-quota-reset", docs, "rp-budget-demo-armed", "docs/token-budgets.md");
        Contains("token-quota-reset", docs, "rp-budget-demo-generation", "docs/token-budgets.md");
    }

    [Fact]
    public async Task ApiCenterAndMcpDiscovery_VerifierSelfTestExecutesAndPasses()
    {
        (int exit, string output) = await RunProcessAsync("api-center-mcp-discovery", "pwsh",
            "-NoProfile", "-NonInteractive", "-File", Path.Combine(Root, "scripts", "Verify-ApiCenter.ps1"), "-SelfTest");
        Invariant("api-center-mcp-discovery", exit == 0, $"Verify-ApiCenter.ps1 -SelfTest exited {exit}: {output}");
        Contains("api-center-mcp-discovery", output, "Self-test passed.", "Verify-ApiCenter.ps1 -SelfTest output");
        Invariant("api-center-mcp-discovery", !output.Contains("[FAIL]", StringComparison.Ordinal), $"self-test reported a failure: {output}");
        Contains("api-center-mcp-discovery", output, "[PASS] self-test: generated-ID synchronized graph passes", "Verify-ApiCenter.ps1 -SelfTest output");

        Contains("api-center-mcp-discovery", Read("infra", "modules", "apim-mcp-api.bicep"), "subscriptionRequired", "infra/modules/apim-mcp-api.bicep");
        Invariant("api-center-mcp-discovery", File.Exists(Path.Combine(Root, "infra", "modules", "api-center.bicep")), "infra/modules/api-center.bicep is missing");
    }

    [Fact]
    public async Task RegulatedMode_IsMetadataOnlyAndAlwaysBypassesCache()
    {
        string api = Read("infra", "modules", "apim-openai-api.bicep");
        Contains("regulated-metadata-only", api, "var bodiesDisabled = gatewaySafetyEnabled || regulatedRetailMode", "apim-openai-api.bicep");
        Contains("regulated-metadata-only", api, "bytes: bodiesDisabled ? 0 : 8192", "apim-openai-api.bicep");
        Contains("regulated-metadata-only", api, "logs: bodiesDisabled ? 'disabled' : 'enabled'", "apim-openai-api.bicep");
        Contains("regulated-cache-bypass", api, "'{cache-regulated}', string(regulatedRetailMode)", "apim-openai-api.bicep");
        Contains("regulated-fail-closed", api, "regulatedRetailMode ? 'regulated' : gatewaySafetyMode", "apim-openai-api.bicep");

        // Render the real inbound fragment with regulated=true and prove the regulated decision precedes every pattern check.
        string rendered = Read("infra", "modules", "apim-openai-semantic-cache-inbound.xml")
            .Replace("{cache-regulated}", "true").Replace("{cache-partition}", "lowes-retail")
            .Replace("{cache-score-threshold}", "0.05").Replace("{cache-embeddings-backend-id}", "emb");
        XElement root = XDocument.Parse("<r>" + rendered + "</r>").Root!;
        string Var(string name) => root.Descendants("set-variable").Single(v => (string?)v.Attribute("name") == name).Attribute("value")!.Value;
        Invariant("regulated-cache-bypass", Var("rp-cache-regulated") == "true", "rendered regulated fragment must set rp-cache-regulated=true");
        string decision = Var("rp-cache-decision");
        int regulated = decision.IndexOf("rp-cache-regulated", StringComparison.Ordinal);
        int firstOther = MyRegex().Matches(decision).Select(m => m.Index).DefaultIfEmpty(int.MaxValue).Min();
        Invariant("regulated-cache-bypass", regulated >= 0 && regulated < firstOther && decision.Contains("return \"regulated\"", StringComparison.Ordinal),
            "regulated bypass must be the first decision and return the closed reason 'regulated'");
        Invariant("regulated-cache-bypass", Var("rp-cache-partition") == "lowes-retail", "walkthrough partition value must render verbatim");

        // Real offline probes: regulated fail-closed on a safety fault, metadata only.
        await RunProbeSelfTestAsync("regulated-fail-closed", "probe-gateway-safety.cjs",
            "[ok]   fault-503:regulated-fail-closed", "[ok]   fault-503:regulated-allowed");
        await RunProbeSelfTestAsync("regulated-cache-bypass", "probe-semantic-cache.cjs", "PASS self-test: conforming gateway passes");

        var headers = new Dictionary<string, string> { [GatewayEvidenceHeaders.Cache] = "bypass", [GatewayEvidenceHeaders.CacheReason] = "regulated" };
        GatewayCallEvidence e = GatewayEvidenceHeaders.Parse(n => headers.GetValueOrDefault(n), 200);
        Invariant("regulated-cache-bypass", e.Cache == GatewayOutcome.Bypass, $"regulated bypass parsed as {e.Cache}");
    }

    [Fact]
    public async Task SemanticCache_ProbeProvesHitWithoutBackendAndClosedVocabularyBypass()
    {
        await RunProbeSelfTestAsync("semantic-cache-hit-bypass", "probe-semantic-cache.cjs",
            "PASS self-test: conforming gateway passes", "PASS self-test: hit that contacts backend fails",
            "PASS self-test: caller-header partition fails", "PASS self-test: cache-unavailable fall-through passes");

        var hit = new Dictionary<string, string> { [GatewayEvidenceHeaders.Cache] = "hit" };
        Invariant("semantic-cache-hit-bypass", GatewayEvidenceHeaders.Parse(n => hit.GetValueOrDefault(n), 200).Cache == GatewayOutcome.Hit,
            "Control Room parser does not report a cache hit");
    }

    [GeneratedRegex(@"```(?<lang>powershell|bash)\r?\n(?<code>.*?)```", RegexOptions.Singleline)]
    private static partial Regex CodeBlockRegex();

    [GeneratedRegex("integrated Azure operating model", RegexOptions.IgnoreCase)]
    private static partial Regex IntegratedClaimRegex();

    [GeneratedRegex("not exclusive", RegexOptions.IgnoreCase)]
    private static partial Regex NotExclusiveRegex();

    [GeneratedRegex(@"\b(only|unique(ly)?|exclusive(ly)?)\s+(to\s+)?(Azure|APIM)\b|\bnobody else\b|\bno other (vendor|platform)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExclusivityRegex();

    [GeneratedRegex(@"\b(sk-[A-Za-z0-9]{16,}|\d{3}-\d{2}-\d{4})\b")]
    private static partial Regex SecretLikeRegex();

    private static string WalkthroughSection()
    {
        string doc = Read("docs", "demo-walkthrough.md");
        Invariant("walkthrough-section", doc.Contains("## Retailer AI Gateway Walkthrough", StringComparison.Ordinal), "docs/demo-walkthrough.md has no 'Retailer AI Gateway Walkthrough' section");
        string section = doc[doc.IndexOf("## Retailer AI Gateway Walkthrough", StringComparison.Ordinal)..];
        int next = section.IndexOf("\n## ", 5, StringComparison.Ordinal);
        return next > 0 ? section[..next] : section;
    }

    private static (string Lang, string Code)[] Blocks(string section, string marker) =>
        [.. CodeBlockRegex().Matches(section).Select(m => (Lang: m.Groups["lang"].Value, Code: m.Groups["code"].Value)).Where(b => b.Code.Contains(marker, StringComparison.Ordinal))];

    private static readonly string[] _contractVariables =
    [
        "APIM_NAME", "RESOURCE_GROUP", "APIM_BASE_URL", "DEPLOYMENT", "API_VERSION", "APIM_SUBSCRIPTION_KEY", "FAILOVER_DEMO_KEY", "SUBSCRIPTION_KEY",
    ];

    // Every variable a block uses must be validated before the block arms, and the base URL must not double the path.
    private static void AssertEnvContract(string id, string lang, string code, int armAt)
    {
        string before = code[..armAt];
        string[] used = [.. _contractVariables.Where(v => lang == "powershell"
            ? Regex.IsMatch(code, @"\$env:" + v + @"\b")
            : Regex.IsMatch(code, @"\$\{?" + v + @"\b"))];
        Invariant(id, used.Length >= 6, $"{lang} block uses too few contract variables ({string.Join(',', used)})");
        foreach (string v in used)
        {
            Invariant(id, before.Contains($"'{v}'", StringComparison.Ordinal) || before.Contains($"\"${{{v}:?}}\"", StringComparison.Ordinal),
                $"{lang} block must validate {v} before arming");
        }

        Invariant(id, !code.Contains("/inference/openai", StringComparison.Ordinal), "APIM_BASE_URL already includes /inference/openai; the block must not append it again");
        Invariant(id, code.Contains("/deployments/", StringComparison.Ordinal), "block must call /deployments/ under APIM_BASE_URL");
    }

    // Within ONE block: arm only after try/trap is in place, disarm only in finally/trap, bump before disarm, verify after disarm.
    private static void AssertFailSafeBlock(string id, string lang, string code, string armName, string? bumpName)
    {
        string arm = $"--named-value-id {armName} --value true";
        string disarm = $"--named-value-id {armName} --value false";
        string show = $"nv show";
        Invariant(id, code.Split(arm).Length == 2, $"{lang} block must arm {armName} exactly once");
        int armAt = code.IndexOf(arm, StringComparison.Ordinal);
        int disarmAt = code.IndexOf(disarm, StringComparison.Ordinal);
        int verifyAt = code.IndexOf(show, disarmAt < 0 ? 0 : disarmAt, StringComparison.Ordinal);
        Invariant(id, disarmAt > 0 && verifyAt > disarmAt, $"{lang} block must disarm {armName} and then verify the disarmed state with 'nv show'");
        if (lang == "powershell")
        {
            int tryAt = code.IndexOf("try {", StringComparison.Ordinal);
            int finallyAt = code.IndexOf("finally {", StringComparison.Ordinal);
            Invariant(id, tryAt >= 0 && tryAt < armAt && armAt < finallyAt, "PowerShell arm must be inside try, before finally");
            Invariant(id, finallyAt < disarmAt && disarmAt < verifyAt, "PowerShell disarm and verification must be inside finally");
            Invariant(id, code.Contains("exit 1", StringComparison.Ordinal) && code.Contains("curl.exe", StringComparison.Ordinal)
                && code.Contains("[System.IO.Path]::GetTempPath()", StringComparison.Ordinal) && !code.Contains("curl -", StringComparison.Ordinal),
                "PowerShell block must use curl.exe, Path.GetTempPath and fail when not disarmed");
            Invariant(id, !code.Contains("$APIM", StringComparison.Ordinal) && code.Contains("$env:", StringComparison.Ordinal), "PowerShell block must use $env: variables");
        }
        else
        {
            int trapAt = code.IndexOf("trap ", StringComparison.Ordinal);
            Invariant(id, trapAt >= 0 && trapAt < armAt && code.Contains(" EXIT", StringComparison.Ordinal), "bash trap ... EXIT must be set before arming");
            int fnAt = code.IndexOf("() {", StringComparison.Ordinal);
            Invariant(id, fnAt >= 0 && fnAt < disarmAt && disarmAt < trapAt && verifyAt < trapAt, "bash disarm and verification must live in the trap handler");
            Invariant(id, code.Contains("set -euo pipefail", StringComparison.Ordinal) && code.Contains("mktemp", StringComparison.Ordinal), "bash block must fail fast and use mktemp");
        }

        AssertEnvContract(id, lang, code, armAt);
        int waitAt = lang == "powershell"
            ? code.IndexOf("Start-Sleep", StringComparison.Ordinal)
            : code.IndexOf("sleep ", StringComparison.Ordinal);
        int firstRequestAt = code.IndexOf("curl", StringComparison.Ordinal);
        Invariant(id, waitAt > armAt && firstRequestAt > waitAt,
            "named-value propagation wait must occur after arming and before the first request");

        if (bumpName is not null)
        {
            int pauseAt = lang == "powershell" ? code.IndexOf("Read-Host", StringComparison.Ordinal) : code.IndexOf("read -r", StringComparison.Ordinal);
            int cleanupAt = lang == "powershell" ? code.IndexOf("finally {", StringComparison.Ordinal) : code.IndexOf("() {", StringComparison.Ordinal);
            int bumpFirst = code.IndexOf($"--named-value-id {bumpName} --value", StringComparison.Ordinal);
            Invariant(id, pauseAt > armAt, "the quota block must pause after arming so the operator can send the app prompt and observe the Control Room 429");
            if (lang == "powershell")
            {
                Invariant(id, pauseAt < cleanupAt, "PowerShell pause must be inside the armed try window, before finally");
            }
            else
            {
                Invariant(id, pauseAt > code.IndexOf("trap ", StringComparison.Ordinal) && cleanupAt < code.IndexOf("trap ", StringComparison.Ordinal), "bash pause must come after the trap is set and the handler is defined");
            }

            Invariant(id, lang == "bash" || pauseAt < bumpFirst, "the pause must precede the generation bump, disarm and verification");
            Invariant(id, code.Contains("429", StringComparison.Ordinal) && code.Contains("Control Room", StringComparison.Ordinal), "the pause prompt must tell the operator to observe the Control Room 429");
            int bumpAt = code.IndexOf($"--named-value-id {bumpName} --value", StringComparison.Ordinal);
            Invariant(id, bumpAt >= 0 && bumpAt < disarmAt, $"{bumpName} must be bumped before the demo is disarmed");
            int cleanupStart = lang == "powershell" ? code.IndexOf("finally {", StringComparison.Ordinal) : code.IndexOf("() {", StringComparison.Ordinal);
            Invariant(id, bumpAt > cleanupStart, "the generation bump must run in the cleanup path so a failed probe still resets");
        }
    }

    [Fact]
    public void Walkthrough_DocumentsSevenPrioritiesFourPacksAndExactSetupResetCommands()
    {
        string section = WalkthroughSection();

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
            "azd env set AZURE_APIM_FAILOVER_DEMO_ENABLED true",
            "azd env set AZURE_APIM_TOKEN_BUDGET_DEMO_ENABLED true",
            "azd env set AZURE_APIM_GATEWAY_SAFETY_ENABLED true",
            "azd env set AZURE_SEMANTIC_CACHE_ENABLED true",
            "azd env set AZURE_APIM_SEMANTIC_CACHE_POLICY_ENABLED true",
            "azd env set AZURE_API_CENTER_ENABLED true",
            "azd env set AZURE_APIM_SEMANTIC_CACHE_PARTITION lowes-retail",
            "azd env set AZURE_REGULATED_RETAIL_MODE true",
            "azd env set AZURE_REGULATED_RETAIL_MODE false",
            "Packs__Active=",
            "$env:Packs__Active=",
            "node scripts/probe-failover-demo.cjs",
            "node scripts/probe-gateway-safety.cjs",
            "node scripts/probe-token-budget.cjs",
            "node scripts/probe-semantic-cache.cjs",
            "pwsh ./scripts/Verify-ApiCenter.ps1 -SelfTest",
        })
        {
            Contains("walkthrough-commands", section, command, "gateway walkthrough");
        }

        foreach (string variable in new[]
        {
            "APIM_NAME", "RESOURCE_GROUP", "APIM_BASE_URL", "DEPLOYMENT", "API_VERSION", "APIM_SUBSCRIPTION_KEY", "FAILOVER_DEMO_KEY",
            "SUBSCRIPTION_KEY", "KEY_SCOPE_A", "KEY_SCOPE_B", "EXPECT_SCOPE_A", "EXPECT_SCOPE_B", "/inference/openai",
        })
        {
            Contains("walkthrough-env-vars", section, variable, "gateway walkthrough");
        }

        // Every variable each probe script reads must be in the documented contract.
        foreach ((string script, string[] vars) in new[]
        {
            ("probe-semantic-cache.cjs", new[] { "APIM_BASE_URL", "DEPLOYMENT", "API_VERSION", "SUBSCRIPTION_KEY" }),
            ("probe-token-budget.cjs", ["KEY_SCOPE_A", "KEY_SCOPE_B", "EXPECT_SCOPE_A", "EXPECT_SCOPE_B", "APIM_NAME", "RESOURCE_GROUP"]),
        })
        {
            string source = File.ReadAllText(Path.Combine(Root, "scripts", script));
            foreach (string v in vars)
            {
                Contains("walkthrough-probe-env", source, v, $"scripts/{script}");
                Contains("walkthrough-probe-env", section, v, "gateway walkthrough");
            }
        }

        Contains("walkthrough-live-ownership", section, "#341", "gateway walkthrough");
        Contains("walkthrough-live-ownership", section, "#342", "gateway walkthrough");
        Invariant("walkthrough-claims", IntegratedClaimRegex().IsMatch(section), "claim must be framed as an integrated Azure operating model");
        Invariant("walkthrough-claims", NotExclusiveRegex().IsMatch(section), "walkthrough must say the capabilities are not exclusive features");
        Invariant("walkthrough-claims", !ExclusivityRegex().IsMatch(section), "walkthrough makes an exclusivity claim");
        Invariant("walkthrough-no-real-data", !SecretLikeRegex().IsMatch(section), "walkthrough contains a secret or identifier-like value");
    }

    [Fact]
    public void Walkthrough_FailoverAndQuotaBlocksAreFailSafeInPowerShellAndBash()
    {
        string section = WalkthroughSection();
        foreach (string lang in new[] { "powershell", "bash" })
        {
            (string Lang, string Code)[] failover = [.. Blocks(section, "rp-failover-demo-armed --value true").Where(b => b.Lang == lang)];
            Invariant("walkthrough-failover-failsafe", failover.Length == 1, $"expected exactly one {lang} failover block, found {failover.Length}");
            AssertFailSafeBlock("walkthrough-failover-failsafe", lang, failover[0].Code, "rp-failover-demo-armed", null);

            (string Lang, string Code)[] quota = [.. Blocks(section, "rp-budget-demo-armed --value true").Where(b => b.Lang == lang)];
            Invariant("walkthrough-quota-failsafe", quota.Length == 1, $"expected exactly one {lang} quota block, found {quota.Length}");
            AssertFailSafeBlock("walkthrough-quota-failsafe", lang, quota[0].Code, "rp-budget-demo-armed", "rp-budget-demo-generation");
        }

        Invariant("walkthrough-failsafe-only", CodeBlockRegex().Matches(section).All(m => !m.Groups["code"].Value.Contains("--value true", StringComparison.Ordinal)
            || m.Groups["code"].Value.Contains("finally {", StringComparison.Ordinal) || m.Groups["code"].Value.Contains("trap ", StringComparison.Ordinal)),
            "an arm command exists outside a try/finally or trap block");
    }

    [Fact]
    public void Walkthrough_RegulatedModeIsSeparateStateAndNeverPrecedesCache()
    {
        string section = WalkthroughSection();
        string[] rows = [.. section.Split('\n').Where(l => (l.StartsWith("| ", StringComparison.Ordinal) && l.Contains("`-retail", StringComparison.Ordinal)) || l.Contains("-retail` |", StringComparison.Ordinal))];
        int cache = Array.FindIndex(rows, r => r.Contains("7. Semantic cache", StringComparison.Ordinal));
        int regulated = Array.FindIndex(rows, r => r.Contains("6. Regulated-retail mode", StringComparison.Ordinal));
        int failover = Array.FindIndex(rows, r => r.Contains("2. Authorized failover", StringComparison.Ordinal));
        int quota = Array.FindIndex(rows, r => r.Contains("4. Token quota and reset", StringComparison.Ordinal));
        Invariant("walkthrough-regulated-order", cache >= 0 && regulated >= 0 && failover >= 0 && quota >= 0, "narrative rows for cache, failover, quota and regulated are required");
        Invariant("walkthrough-regulated-order", cache < regulated && failover < regulated && quota < regulated, "regulated mode must come after every normal-mode step, including the cache demo");
        Invariant("walkthrough-regulated-order", rows[regulated].Contains("pre-provisioned", StringComparison.OrdinalIgnoreCase) || rows[regulated].Contains("captured", StringComparison.OrdinalIgnoreCase),
            "the regulated row must say it is pre-provisioned or captured");
        foreach (string row in rows.Where(r => r.Contains("regulated", StringComparison.OrdinalIgnoreCase) && !ReferenceEquals(r, rows[regulated])))
        {
            Invariant("walkthrough-regulated-order", row.Contains("regulated` ", StringComparison.Ordinal) || row.Contains("always bypass", StringComparison.OrdinalIgnoreCase) || row.Contains("regulated mode always", StringComparison.OrdinalIgnoreCase),
                $"a normal-mode row mentions regulated mode: {row}");
        }

        foreach (string row in rows)
        {
            Invariant("walkthrough-no-live-reprovision", !row.Contains("azd up", StringComparison.OrdinalIgnoreCase) && !row.Contains("reprovision", StringComparison.OrdinalIgnoreCase),
                "no timed narrative row may imply an azd (re)provision");
        }

        Contains("walkthrough-regulated-cache", section, "What is your return policy?", "gateway walkthrough");
        Contains("walkthrough-regulated-cache", section, "node scripts/probe-semantic-cache.cjs", "gateway walkthrough");
        Contains("walkthrough-regulated-cache", section, "tool-free direct-APIM", "gateway walkthrough");
        Contains("walkthrough-regulated-cache", section, "out-of-band APIM external-cache registration", "gateway walkthrough");
        Contains("walkthrough-regulated-cache", section, "AZURE_SEMANTIC_CACHE_PUBLIC_NETWORK_ACCESS", "gateway walkthrough");
        Contains("walkthrough-regulated-cache", section, "AZURE_SEMANTIC_CACHE_PRIVATE_ENDPOINT_SUBNET_ID", "gateway walkthrough");
        Contains("walkthrough-regulated-cache", section, "drop the cache-header claim", "gateway walkthrough");
        Contains("walkthrough-regulated-cache", section, "sequential pre-capture", "gateway walkthrough");
        Contains("walkthrough-regulated-cache", section, "azd env get-value AZURE_APIM_INFERENCE_ENDPOINT", "gateway walkthrough");
        Contains("walkthrough-regulated-cache", section, "x-rp-gw-cache: bypass", "gateway walkthrough");
        Contains("walkthrough-regulated-cache", section, "PROBE_BENIGN_PROMPT", "gateway walkthrough");
        int select = section.IndexOf("azd env select <normal-env-name>", StringComparison.Ordinal);
        int restore = section.IndexOf("azd env set AZURE_REGULATED_RETAIL_MODE false", StringComparison.Ordinal);
        Invariant("walkthrough-regulated-restore", select > 0 && restore > select, "restore must select the normal env before setting regulated=false");
        Contains("walkthrough-regulated-order", section, "never** toggled during the walkthrough", "gateway walkthrough");
        Contains("walkthrough-regulated-order", section, "azd env new retailpulse-regulated", "gateway walkthrough");
        int regulatedSetup = section.IndexOf("azd env set AZURE_REGULATED_RETAIL_MODE true", StringComparison.Ordinal);
        Invariant("walkthrough-regulated-order", regulatedSetup > section.IndexOf("azd env new retailpulse-regulated", StringComparison.Ordinal),
            "regulated provisioning must happen in its own azd environment");
    }

    [GeneratedRegex("rp-cache-(?!regulated|decision)[a-z-]+")]
    private static partial Regex MyRegex();
}

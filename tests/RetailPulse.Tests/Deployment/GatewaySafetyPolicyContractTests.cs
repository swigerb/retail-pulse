using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;

namespace RetailPulse.Tests.Deployment;

/// <summary>
/// Offline contract for APIM-level Prompt Shields / text harm enforcement (#331). Inspects the
/// policy fragments and Bicep source only; never touches Azure. Behavior that cannot run without
/// an APIM runtime (benign, attack, severity boundary, outbound block, dependency errors, disabled
/// mode) is proven by asserting the exact decision code paths and their ordering in the policy.
/// </summary>
public sealed partial class GatewaySafetyPolicyContractTests
{
    private static readonly string Modules = Path.Combine(FindRepoRoot(), "infra", "modules");

    private static string Read(string name) => File.ReadAllText(Path.Combine(Modules, name));

    private static string Inbound => Read("apim-openai-safety-inbound.xml");

    private static string Outbound => Read("apim-openai-safety-outbound.xml");

    private static string BasePolicy => Read("apim-openai-policy.xml");

    private static string Bicep => Read("apim-openai-api.bicep");

    private static string Unescape(string xml) => System.Net.WebUtility.HtmlDecode(xml);

    [Fact]
    public void Fragments_AreWellFormedXml()
    {
        foreach (string fragment in new[] { Inbound, Outbound })
        {
            Action parse = () => XDocument.Parse("<r>" + fragment + "</r>");
            parse.Should().NotThrow();
        }
    }

    [Fact]
    public void PolicyOrdering_SafetyRunsBeforeModelBackendAndTokenLimit_AndBeforeOutboundDelivery()
    {
        BasePolicy.IndexOf("{safety-inbound}", StringComparison.Ordinal).Should().BeGreaterThan(0)
            .And.BeLessThan(BasePolicy.IndexOf("<set-backend-service", StringComparison.Ordinal),
                "a blocked prompt must never reach backend selection, so the model is not invoked");
        BasePolicy.IndexOf("{safety-inbound}", StringComparison.Ordinal)
            .Should().BeLessThan(BasePolicy.IndexOf("{budget-limit}", StringComparison.Ordinal));
        BasePolicy.IndexOf("{safety-inbound}", StringComparison.Ordinal)
            .Should().BeGreaterThan(BasePolicy.IndexOf("x-rp-gw-safety-output-checked", StringComparison.Ordinal),
                "spoofable caller evidence headers are stripped first");
        BasePolicy.IndexOf("{safety-outbound}", StringComparison.Ordinal)
            .Should().BeGreaterThan(BasePolicy.IndexOf("<outbound>", StringComparison.Ordinal))
            .And.BeLessThan(BasePolicy.IndexOf("x-rp-gw-request-id\" exists-action=\"override", StringComparison.Ordinal));
        BasePolicy.Should().NotContain("<cache-store").And.NotContain("<cache-lookup",
            "no cache policy may store a response before the outbound safety check");
    }

    [Fact]
    public void Inbound_UsesManagedIdentityAndGaRestVersion_WithoutKeys()
    {
        string x = Unescape(Inbound);
        x.Should().Contain("<authentication-managed-identity resource=\"https://cognitiveservices.azure.com\"");
        x.Should().Contain("text:shieldPrompt?api-version=2024-09-01");
        x.Should().Contain("text:analyze?api-version=2024-09-01");
        x.Should().Contain("{cs-endpoint}");
        x.Should().NotContainEquivalentOf("Ocp-Apim-Subscription-Key");
        x.Should().Contain("preserveContent: true", "the request body must still reach the model after evaluation");
        x.Should().Contain("timeout=\"5\"").And.Contain("ignore-error=\"true\"");
    }

    [Fact]
    public void Inbound_BenignPrompt_Allows_AndAttackOrSeverityBoundaryBlocks()
    {
        string x = Unescape(Inbound);
        x.Should().Contain("attackDetected").And.Contain("return a ? \"attack\" : \"clean\"");
        x.Should().Contain("if (s == \"attack\" || over) return \"block\";");
        x.Should().Contain(">= {severity-threshold}", "severity at the threshold blocks; below it allows");
        x.Should().Contain("return \"allow\";");
        x.Should().Contain("== \"block\" ? 403 : (string)context.Variables[\"rp-safety-decision\"] == \"reject\" ? 400 : 503");
    }

    [Fact]
    public void DependencyErrors_DemoAllowsWithEvidence_RegulatedAndAuthFailClosed()
    {
        string x = Unescape(Inbound);
        x.Should().Contain("if (s == 408 || s == 429 || (s >= 500 && s <= 599)) return \"dependency\";");
        x.Should().Contain("if (s == 401 || s == 403) return \"auth\";");
        x.Should().Contain("return \"config\";");
        x.Should().Contain("if (r == null) return \"null\";", "a null response is NOT a confirmed dependency status");
        x.Should().Contain("if ((sOk || s == \"dependency\") && (aOk || a == \"dependency\"))");
        x.Should().Contain("== \"regulated\" ? \"error\" : \"allowed_on_safety_error\"");
        x.Should().Contain("!context.Variables.ContainsKey(\"rp-cs-token\")");
        x.Should().Contain("gateway_safety_unavailable").And.Contain("Content safety could not be verified.");
    }

    [Fact]
    public void Outbound_BlocksBeforeDelivery_AndNeverClaimsPassedForStreaming()
    {
        string x = Unescape(Outbound);
        x.Should().Contain("rp-safety-stream\"]) != \"true\"", "streaming chunks cannot be recalled, so only buffered responses are checked");
        x.Should().Contain("context.Response.StatusCode == 200");
        x.IndexOf("<return-response>", StringComparison.Ordinal).Should().BeGreaterThan(0)
            .And.BeLessThan(x.IndexOf("<value>passed</value>", StringComparison.Ordinal));
        x.Should().Contain("gateway_safety_blocked");
        x.Should().Contain("rp-safety-output-checked\"]) == \"true\"", "passed requires a real output evaluation");
        x.Should().Contain("allowed_on_safety_error");
    }

    [Fact]
    public void NoPayloadLeakage_NothingSensitiveIsLoggedOrPlacedInHeaders()
    {
        foreach (string fragment in new[] { Inbound, Outbound })
        {
            string x = Unescape(fragment);
            x.Should().NotContain("<trace").And.NotContain("log-to-eventhub").And.NotContain("<emit-metric");
            var doc = XDocument.Parse("<r>" + fragment + "</r>");
            foreach (XElement header in doc.Descendants("set-header").Where(h => (string?)h.Attribute("name") != "Authorization"))
            {
                string value = Unescape(header.Value);
                value.Should().NotMatchRegex("rp-(cs-|shield-|analyze-|out-)(?!decision|result)",
                    "headers carry decision metadata only, never bodies, tokens or raw safety results");
                value.Should().NotContain("rp-cs-token").And.NotContain("Body");
            }

            foreach (XElement header in doc.Descendants("set-header").Where(h => ((string?)h.Attribute("name"))?.StartsWith("x-rp-gw-safety-", StringComparison.Ordinal) == true))
            {
                Unescape(header.Value).Should().MatchRegex(@"^@\(context\.Variables\[""rp-safety-[a-z-]+""\](\.ToString\(\)|)\)$|^@\(\(\(int\)context\.Variables\[""rp-safety-ms""\]\)\.ToString\(\)\)$");
            }
        }

        Unescape(Inbound).Should().Contain("The request was blocked by gateway content safety.");
    }

    [Fact]
    public void DisabledMode_RendersNoSafetyPolicyAndNoRoleAssignment_ButStripsSpoofedEvidence()
    {
        Bicep.Should().Contain("param gatewaySafetyEnabled bool = false");
        Bicep.Should().Contain("var safetyInboundFragment = gatewaySafetyEnabled").And.Contain(": ''");
        Bicep.Should().Contain("var safetyOutboundFragment = gatewaySafetyEnabled");
        Bicep.Should().Contain("if (gatewaySafetyEnabled)");
        foreach (string h in new[] { "phase", "decision", "prompt-attack", "category", "severity", "mode", "latency-ms", "output-checked" })
        {
            BasePolicy.Should().Contain($"<set-header name=\"x-rp-gw-safety-{h}\" exists-action=\"delete\" />");
        }
    }

    [Fact]
    public void Bicep_ReusesContentSafetyResourceAndGrantsResourceScopedRole_Idempotently()
    {
        Bicep.Should().Contain("var cognitiveServicesUserRoleId = 'a97b65f3-24c7-4388-baec-2e87135dc908'");
        Bicep.Should().Contain("resource contentSafetyAccount 'Microsoft.CognitiveServices/accounts@2024-10-01' existing");
        Bicep.Should().Contain("scope: resourceGroup(contentSafetyResourceGroupName)");
        Bicep.Should().Contain("aiServicesName: contentSafetyAccountName");
        Bicep.Should().Contain("principalId: apimPrincipalId");
        Bicep.Should().Contain("roleDefinitionId: cognitiveServicesUserRoleId");
        Bicep.Should().Contain("@allowed([\n  'demo'\n  'regulated'\n])");

        string role = Read("apim-openai-role-assignment.bicep");
        role.Should().Contain("guid(aiServices.id, principalId, roleDefinitionId)", "deterministic names make redeploys idempotent");
        role.Should().Contain("scope: aiServices");

        string main = File.ReadAllText(Path.Combine(Modules, "..", "main.bicep"));
        main.Should().Contain("gatewaySafetyEnabled: (apimGatewaySafetyEnabled || regulatedRetailMode) && contentSafetyOn");
        main.Should().Contain("contentSafetyAccountName: contentSafetyOn ? contentSafety!.outputs.name : ''");
    }

    [GeneratedRegex(@"var maskedRequestIdExpr = '(?<expr>[^\n]*)'\r?\n")]
    private static partial Regex MaskedRequestIdRegex();

    [Fact]
    public void RegulatedRetailMode_MaskedRequestIdExpression_RendersXmlSafePolicy()
    {
        Match m = MaskedRequestIdRegex().Match(Bicep);
        m.Success.Should().BeTrue();
        string expr = m.Groups["expr"].Value;
        expr.Should().NotContain("<", "raw < is invalid XML inside the rendered policy");
        expr.Should().Contain("&lt;=");

        string rendered = BasePolicy.Replace("{safety-inbound}", Inbound).Replace("{safety-outbound}", Outbound)
            .Replace("@(context.RequestId.ToString())", expr);
        XDocument doc = null!;
        Action parse = () => doc = XDocument.Parse(rendered.Replace("{profile-outbound}", string.Empty));
        parse.Should().NotThrow("the regulated rendered policy must be well-formed XML");
        doc.Descendants("value").Select(v => v.Value)
            .Should().Contain(v => v.Contains("r.Length <= 8"), "XML parsing unescapes the expression for APIM");
    }

    [Fact]
    public void RegulatedRetailMode_IsWiredToApiContainerApp_AsGatewayRegulatedRetailEnabled()
    {
        string main = File.ReadAllText(Path.Combine(Modules, "..", "main.bicep"));
        main.Should().MatchRegex(@"(?m)^\s+regulatedRetailMode: regulatedRetailMode\s*$");
        string apps = Read("container-apps.bicep");
        apps.Should().Contain("param regulatedRetailMode bool = false", "disabled by default");
        apps.Should().MatchRegex(@"name: 'Gateway__RegulatedRetail__Enabled'\s+value: string\(regulatedRetailMode\)");
    }

    [Fact]
    public void RegulatedRetailMode_ForcesBodyFreeDiagnostics_FailClosed_MaskedIds_AndNoCache()
    {
        Bicep.Should().Contain("param regulatedRetailMode bool = false");
        Bicep.Should().Contain("var effectiveSafetyMode = regulatedRetailMode ? 'regulated' : gatewaySafetyMode");
        Bicep.Should().Contain("var bodiesDisabled = gatewaySafetyEnabled || regulatedRetailMode");
        Bicep.Should().Contain("regulatedRetailMode ? maskedRequestIdExpr");
        Bicep.Should().Contain("regulated-retail-non-hipaa");
        string apim = Read("apim.bicep");
        apim.Should().Contain("bytes: captureRequestBodies ? 8192 : 0");
        string main = File.ReadAllText(Path.Combine(Modules, "..", "main.bicep"));
        main.Should().Contain("var contentSafetyOn = contentSafetyEnabled || regulatedRetailMode");
        main.Should().Contain("regulatedRetailMode: regulatedRetailMode && contentSafetyOn");
        BasePolicy.Should().Contain("{profile-outbound}").And.NotContain("<cache-lookup").And.NotContain("<cache-store");
        string doc = File.ReadAllText(Path.Combine(Modules, "..", "..", "docs", "ai-gateway-integration.md"));
        doc.Should().Contain("not HIPAA certification").And.Contain("patient data").And.Contain("clinical advice");
    }

    [Fact]
    public void Docs_DescribeFailureModesAndStreamingLimitation()
    {
        string doc = File.ReadAllText(Path.Combine(Modules, "..", "..", "docs", "ai-gateway-integration.md"));
        doc.Should().Contain("allowed_on_safety_error").And.Contain("regulated").And.Contain("streaming");
        doc.Should().Contain("never claims a final 403");
    }

    [Fact]
    public void Policy_NeverTruncatesContent_AndFailsClosedOnOversizedOrUnparseableInput()
    {
        foreach (string fragment in new[] { Inbound, Outbound })
        {
            Unescape(fragment).Should().NotContain(".Substring(", "truncation would let unchecked content reach the model or client");
        }

        Unescape(Inbound).Should().Contain("return \"oversized\"").And.Contain("return \"invalid\"");
        Unescape(Outbound).Should().Contain("return u.Length > 10000 ? \"oversized\" : \"ok\"");
    }

    [Fact]
    public void OutputChecked_IsSetOnlyAfterAParsedSuccessfulAnalysis()
    {
        string x = Unescape(Outbound);
        x.Should().Contain("StartsWith(\"ok:\") ? \"true\" : \"false\"");
        x.IndexOf("name=\"rp-safety-output-checked\"", StringComparison.Ordinal)
            .Should().BeGreaterThan(x.IndexOf("name=\"rp-out-result\"", StringComparison.Ordinal));
    }

    [Fact]
    public void PolicyExpressions_DoNotUseUnsupportedSystemFuncDelegates()
    {
        Unescape(Inbound).Should().NotContain("Func<JToken", "System.Func is not in APIM's policy-expression allow-list");
        Unescape(Outbound).Should().NotContain("Func<JToken", "System.Func is not in APIM's policy-expression allow-list");
    }

    [Fact]
    public void Diagnostics_CaptureNoBodiesOrMessageContent_WhenGatewaySafetyIsEnabled()
    {
        Bicep.Should().Contain("bytes: bodiesDisabled ? 0 : 8192");
        Bicep.Should().Contain("logs: bodiesDisabled ? 'disabled' : 'enabled'");
        Bicep.Should().Contain("messages: 'all'", "the only supported message selector is retained behind disabled LLM logging");
        Bicep.Should().NotContain("bytes: 8192\n", "every body capture size must be conditional on gateway safety");
        string apim = Read("apim.bicep");
        apim.Should().Contain("bytes: captureRequestBodies ? 8192 : 0").And.NotContain("bytes: 8192\n");
        File.ReadAllText(Path.Combine(Modules, "..", "main.bicep"))
            .Should().Contain("captureRequestBodies: !((apimGatewaySafetyEnabled || regulatedRetailMode) && contentSafetyOn)");
    }

    [Fact]
    public void ExecutableSelfTest_RunsRealPolicyExpressionsThroughDecisionMatrix()
    {
        string project = Path.Combine(FindRepoRoot(), "scripts", "gateway-safety-selftest", "GatewaySafetySelfTest.csproj");
        var psi = new ProcessStartInfo("dotnet", $"run --project \"{project}\" -c Release")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
        using Process p = Process.Start(psi)!;
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(280_000).Should().BeTrue();
        p.ExitCode.Should().Be(0, output + await_(stderr));
        output.Should().Contain("SELFTEST PASS").And.NotContain("FAIL [");
        output.Should().Contain("[benign]").And.Contain("[streaming]").And.Contain("[oversized:user]")
            .And.Contain("[demo:shield-408]").And.Contain("[closed:analyze-null]").And.Contain("[outbound:block]");
    }

    private static string await_(Task<string> t) => t.GetAwaiter().GetResult();

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

        throw new InvalidOperationException("Could not locate repo root");
    }
}

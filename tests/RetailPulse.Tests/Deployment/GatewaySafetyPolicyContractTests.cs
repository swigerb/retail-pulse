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
public sealed class GatewaySafetyPolicyContractTests
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
            .Should().BeLessThan(BasePolicy.IndexOf("<azure-openai-token-limit", StringComparison.Ordinal));
        BasePolicy.IndexOf("{safety-inbound}", StringComparison.Ordinal)
            .Should().BeGreaterThan(BasePolicy.IndexOf("x-rp-gw-safety-output-checked", StringComparison.Ordinal),
                "spoofable caller evidence headers are stripped first");
        BasePolicy.IndexOf("{safety-outbound}", StringComparison.Ordinal)
            .Should().BeGreaterThan(BasePolicy.IndexOf("<outbound>", StringComparison.Ordinal))
            .And.BeLessThan(BasePolicy.IndexOf("x-rp-gw-request-id\" exists-action=\"override", StringComparison.Ordinal));
        Regex.IsMatch(BasePolicy, "<cache-(store|lookup)").Should().BeFalse(
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
        x.Should().Contain("<set-status code=\"@((string)context.Variables[\"rp-safety-decision\"] == \"block\" ? 403 : 503)\"");
    }

    [Fact]
    public void DependencyErrors_DemoAllowsWithEvidence_RegulatedAndAuthFailClosed()
    {
        string x = Unescape(Inbound);
        x.Should().Contain("if (s == 408 || s == 429 || s >= 500) return \"dependency\";");
        x.Should().Contain("if (s == 401 || s == 403) return \"auth\";");
        x.Should().Contain("return \"config\";");
        x.Should().Contain("s == \"auth\" || s == \"config\" || a == \"auth\" || a == \"config\") return \"error\";");
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
            XDocument doc = XDocument.Parse("<r>" + fragment + "</r>");
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
        main.Should().Contain("gatewaySafetyEnabled: apimGatewaySafetyEnabled && contentSafetyEnabled");
        main.Should().Contain("contentSafetyAccountName: contentSafetyEnabled ? contentSafety!.outputs.name : ''");
    }

    [Fact]
    public void Docs_DescribeFailureModesAndStreamingLimitation()
    {
        string doc = File.ReadAllText(Path.Combine(Modules, "..", "..", "docs", "ai-gateway-integration.md"));
        doc.Should().Contain("allowed_on_safety_error").And.Contain("regulated").And.Contain("streaming");
        doc.Should().Contain("never claims a final 403");
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

        throw new InvalidOperationException("Could not locate repo root");
    }
}

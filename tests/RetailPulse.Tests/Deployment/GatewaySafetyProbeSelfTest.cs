using System.Diagnostics;
using FluentAssertions;

namespace RetailPulse.Tests.Deployment;

public sealed class GatewaySafetyProbeSelfTest
{
    private static string Root()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "scripts", "probe-gateway-safety.cjs")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void ProbeScript_SelfTest_AcceptsCorrectGatewayAndRejectsBrokenOnes()
    {
        var psi = new ProcessStartInfo("node", $"\"{Path.Combine(Root(), "scripts", "probe-gateway-safety.cjs")}\" --self-test")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(60_000).Should().BeTrue();
        p.ExitCode.Should().Be(0, output);
        output.Should().Contain("SELFTEST PASS").And.Contain("fault-408:demo-fail-open").And.NotContain("[FAIL]");
    }

    [Fact]
    public void InboundPolicy_FailsClosedOnToolOnlyRequests_WithoutPlaceholderPrompt()
    {
        string xml = File.ReadAllText(Path.Combine(Root(), "infra", "modules", "apim-openai-safety-inbound.xml"));

        xml.Should().Contain("userLen == 0 &amp;&amp; docs &gt; 0) { return &quot;invalid&quot;; }");
        xml.Should().NotContain("&quot;.&quot;");
    }

    [Fact]
    public void Verifier_ChecksResourceScopedRoleOrderedPolicyAndBodyFreeDiagnostics()
    {
        string ps = File.ReadAllText(Path.Combine(Root(), "scripts", "Verify-ApimAiGateway.ps1"));

        ps.Should().Contain("a97b65f3-24c7-4388-baec-2e87135dc908")
            .And.Contain("Test-SafetyPolicyOrder")
            .And.Contain("Test-SafetyDiagnosticsBodyFree")
            .And.Contain("Resolve-GatewaySafetyEnabled")
            .And.Contain("inferred from rendered policy");
    }
}

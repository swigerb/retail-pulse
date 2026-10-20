using System.Diagnostics;
using FluentAssertions;

namespace RetailPulse.Tests.Deployment;

public sealed class FailoverDemoProbeSelfTest
{
    [Fact]
    public void ProbeScript_SelfTest_AcceptsCorrectGatewayAndRejectsBrokenOnes()
    {
        string root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "scripts", "probe-failover-demo.cjs")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        root.Should().NotBeNull();
        var psi = new ProcessStartInfo("node", $"\"{Path.Combine(root!, "scripts", "probe-failover-demo.cjs")}\" --self-test")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(60_000).Should().BeTrue();
        p.ExitCode.Should().Be(0, output);
        output.Should().Contain("[correct]").And.NotContain("FAIL");
    }
}

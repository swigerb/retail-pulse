using System.Text.RegularExpressions;
using FluentAssertions;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Deployment-contract coverage over the scale settings in
/// <c>infra/modules/container-apps.bicep</c>.
/// </summary>
/// <remarks>
/// The API and MCP server were deployed with <c>minReplicas: 0</c>. After an idle period
/// the first visitor paid a cold start: the container app controller logged twenty
/// consecutive <c>startup probe failed: connection refused</c> events over twenty seconds,
/// during which SignalR could not connect (the telemetry pill read "Off") and the first
/// chat hung on "Thinking...". That is indistinguishable from an outage to anyone looking
/// at the screen, and it is the first thing a visitor sees.
///
/// A warm replica is a cost decision, so it stays configurable — but the default has to be
/// the one that does not look broken. No unit test could catch this; the behaviour lives
/// entirely in the deployment manifest.
/// </remarks>
public sealed partial class ContainerAppScaleContractTests
{
    [GeneratedRegex(@"minReplicas:\s*(?<value>[^\r\n]+)")]
    private static partial Regex MinReplicasAssignment();

    [GeneratedRegex(@"param\s+alwaysWarm\s+bool\s*=\s*(?<value>true|false)")]
    private static partial Regex AlwaysWarmDefault();

    private static string ReadBicep(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RetailPulse.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the repository root must be discoverable from the test output directory");
        string path = Path.Combine(directory.FullName, relativePath);
        File.Exists(path).Should().BeTrue($"{relativePath} must exist");

        return File.ReadAllText(path);
    }

    [Fact]
    public void ApiAndMcp_AreWarmByDefault_NotPinnedToZero()
    {
        string bicep = ReadBicep(Path.Combine("infra", "modules", "container-apps.bicep"));

        IReadOnlyList<string> values = [.. MinReplicasAssignment()
            .Matches(bicep)
            .Select(m => m.Groups["value"].Value.Trim())];

        values.Should().NotBeEmpty("the container apps must declare a scale floor");

        // A bare `minReplicas: 0` is the regression: it hardcodes scale-to-zero with no
        // way to keep a customer-facing environment warm.
        values.Should().NotContain(
            "0",
            "a hardcoded scale floor of zero makes the first request after idle look like an outage");
    }

    [Fact]
    public void WarmReplicas_RemainConfigurable()
    {
        string bicep = ReadBicep(Path.Combine("infra", "modules", "container-apps.bicep"));

        // Keeping replicas warm costs money continuously, so an operator running a
        // non-customer-facing environment must be able to opt out.
        bicep.Should().Contain(
            "alwaysWarm",
            "the scale floor must stay configurable rather than being pinned warm");
    }

    [Fact]
    public void WarmReplicas_DefaultToOn()
    {
        string main = ReadBicep(Path.Combine("infra", "main.bicep"));

        Match match = AlwaysWarmDefault().Match(main);

        match.Success.Should().BeTrue("main.bicep must declare the alwaysWarm parameter");
        match.Groups["value"].Value.Should().Be(
            "true",
            "the default has to be the setting that does not greet a visitor with a cold start");
    }

    [Fact]
    public void TeamsBot_StaysWarm_WhenABotIsConfigured()
    {
        string bicep = ReadBicep(Path.Combine("infra", "modules", "container-apps.bicep"));

        // The Bot Framework retries a cold-start timeout, but a scale-to-zero bot drops
        // the first message of a conversation often enough to look broken.
        bicep.Should().Contain(
            "minReplicas: botConfigured ? 1 : 0",
            "a configured bot must keep a warm replica, while an unconfigured one costs nothing");
    }

    [Fact]
    public void AlwaysWarm_IsReadableFromTheEnvironment()
    {
        string bicepparam = ReadBicep(Path.Combine("infra", "main.bicepparam"));

        bicepparam.Should().Contain(
            "AZURE_ALWAYS_WARM",
            "an operator must be able to opt out with azd env set, without editing the template");
    }
}

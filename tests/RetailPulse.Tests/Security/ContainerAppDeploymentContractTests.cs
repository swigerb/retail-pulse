using System.Text.RegularExpressions;
using FluentAssertions;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Deployment-contract coverage over <c>infra/modules/container-apps.bicep</c>.
///
/// <para>
/// Both of the authentication gates in this solution that are keyed to the hosting
/// environment — the MCP server's API-key gate
/// (<c>apiKeyRequired = !IsDevelopment() || ApiKey:Enabled</c>) and the Teams bot's
/// <c>MapAgentApplicationEndpoints(requireAuth: !IsDevelopment())</c> — silently turn
/// themselves OFF when the app runs as <c>Development</c>. Both apps were deployed with
/// <c>ASPNETCORE_ENVIRONMENT=Development</c> behind a public ingress, which left the whole
/// MCP REST and tool surface reachable from the internet with no credential.
/// </para>
///
/// <para>
/// No unit test could see that, because the defect lived entirely in the deployment
/// manifest. These tests read the real Bicep and assert the invariant directly.
/// </para>
/// </summary>
public sealed partial class ContainerAppDeploymentContractTests
{
    [GeneratedRegex(@"^resource\s+(?<symbol>\w+)\s+'Microsoft\.App/containerApps@[^']+'\s*=\s*\{", RegexOptions.Multiline)]
    private static partial Regex ContainerAppDeclaration();

    [GeneratedRegex(@"external:\s*true")]
    private static partial Regex ExternalIngress();

    [GeneratedRegex(@"name:\s*'ASPNETCORE_ENVIRONMENT'\s*value:\s*'(?<value>[^']+)'", RegexOptions.Singleline)]
    private static partial Regex AspNetCoreEnvironmentAssignment();

    private static string BicepPath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RetailPulse.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                "Could not locate the repository root (no RetailPulse.slnx found above the test output directory).");
        }

        string path = Path.Combine(directory.FullName, "infra", "modules", "container-apps.bicep");
        File.Exists(path).Should().BeTrue($"expected the container apps Bicep module at {path}");

        return path;
    }

    private static string BicepText() => File.ReadAllText(BicepPath());

    /// <summary>
    /// Splits the Bicep into one block per <c>resource ... 'Microsoft.App/containerApps@...'</c>
    /// declaration so ingress and env can be correlated per app.
    /// </summary>
    private static IReadOnlyList<(string Name, string Body)> ContainerAppBlocks()
    {
        string text = BicepText();
        MatchCollection starts = ContainerAppDeclaration().Matches(text);

        starts.Should().NotBeEmpty("the module must declare at least one container app");

        var blocks = new List<(string, string)>(starts.Count);

        for (int i = 0; i < starts.Count; i++)
        {
            int start = starts[i].Index;
            int end = i + 1 < starts.Count ? starts[i + 1].Index : text.Length;
            blocks.Add((starts[i].Groups["symbol"].Value, text[start..end]));
        }

        return blocks;
    }

    private static (string Name, string Body) BlockFor(string containerAppName) =>
        ContainerAppBlocks().Single(b => b.Body.Contains(containerAppName, StringComparison.Ordinal));

    private static string? AspNetCoreEnvironment(string body)
    {
        Match match = AspNetCoreEnvironmentAssignment().Match(body);
        return match.Success ? match.Groups["value"].Value : null;
    }

    [Fact]
    [Trait("OWASP", "A05-SecurityMisconfiguration")]
    public void NoContainerApp_IsDeployedAsDevelopment()
    {
        foreach ((string name, string body) in ContainerAppBlocks())
        {
            AspNetCoreEnvironment(body).Should().NotBe(
                "Development",
                $"container app '{name}' must not run as Development — environment-gated auth "
                + "(the MCP API-key gate and the Teams bot channel auth) disables itself there");
        }
    }

    [Fact]
    [Trait("OWASP", "A01-BrokenAccessControl")]
    public void NoPubliclyExposedContainerApp_IsDeployedAsDevelopment()
    {
        foreach ((string name, string body) in ContainerAppBlocks())
        {
            if (!ExternalIngress().IsMatch(body))
            {
                continue;
            }

            AspNetCoreEnvironment(body).Should().NotBe(
                "Development",
                $"container app '{name}' has a public ingress, so running it as Development "
                + "would expose an unauthenticated surface to the internet");
        }
    }

    [Fact]
    [Trait("OWASP", "A01-BrokenAccessControl")]
    public void McpServer_IsNotPubliclyExposed()
    {
        (_, string body) = BlockFor("ca-retailpulse-mcp");

        ExternalIngress().IsMatch(body).Should().BeFalse(
            "the MCP server is a server-to-server dependency of the API and must not be "
            + "reachable from the public internet");
    }

    [Fact]
    [Trait("OWASP", "A01-BrokenAccessControl")]
    public void McpServer_EnforcesItsApiKeyGate()
    {
        (_, string body) = BlockFor("ca-retailpulse-mcp");

        body.Should().MatchRegex(
            @"name:\s*'ApiKey__Enabled'\s*value:\s*'true'",
            "the MCP server must have its API-key gate explicitly enabled");

        body.Should().MatchRegex(
            @"name:\s*'ApiKey__Value'\s*secretRef:",
            "the MCP API key must be supplied from an ACA secret, never a plain env value");
    }

    [Fact]
    [Trait("OWASP", "A01-BrokenAccessControl")]
    public void Api_PresentsTheMcpApiKey()
    {
        (_, string body) = BlockFor("ca-retailpulse-api");

        body.Should().MatchRegex(
            @"name:\s*'McpServer__ApiKey'\s*secretRef:",
            "the API must present the MCP API key, or every MCP call fails once the gate is on");
    }

    [Fact]
    [Trait("OWASP", "A02-CryptographicFailures")]
    public void SharedSecrets_AreDeclaredAsSecureParameters_NotLiterals()
    {
        string text = BicepText();

        text.Should().MatchRegex(
            @"@secure\(\)\s*param\s+mcpApiKey\s+string",
            "the MCP shared secret must be a @secure() parameter");

        text.Should().MatchRegex(
            @"@secure\(\)\s*param\s+apimSubscriptionKey\s+string",
            "the APIM subscription key must be a @secure() parameter");
    }

    [Fact]
    [Trait("OWASP", "A02-CryptographicFailures")]
    public void McpApiKeyDefault_IsDeterministic_SoProvisionIsIdempotent()
    {
        string text = BicepText();

        // newGuid() would mint a fresh key on every provision. The API and the MCP
        // server read it from separate Container Apps secrets, and a secret-value
        // change does not necessarily roll both revisions together — so a rotating
        // default can leave the API presenting a stale key and every MCP call
        // failing 401 until both apps happen to restart.
        text.Should().NotMatchRegex(
            @"param\s+mcpApiKey\s+string\s*=\s*newGuid\(\)",
            "the MCP shared secret must not be regenerated on every provision");

        text.Should().MatchRegex(
            @"param\s+mcpApiKey\s+string\s*=\s*'\$\{uniqueString\(",
            "the MCP shared secret default must be derived deterministically");
    }

    [Fact]
    [Trait("OWASP", "A05-SecurityMisconfiguration")]
    public void EveryContainerApp_RejectsInsecureTransport()
    {
        foreach ((string name, string body) in ContainerAppBlocks())
        {
            body.Should().MatchRegex(
                @"allowInsecure:\s*false",
                $"container app '{name}' must not accept plaintext HTTP on its ingress");
        }
    }

    /// <summary>
    /// Plan-first orchestration is gated on <c>PlanPersistence:Enabled</c>. That single
    /// flag registers <c>IPlanStore</c>, which is what maps <c>/api/plans/*</c> AND wires
    /// the <c>PlanOrchestrator</c>. Deployed with it off, the SPA's "Plan" execution-path
    /// option was an inert control and the Plans panel showed a raw 404 from a route that
    /// was never mapped. Pin it so the deployed surface and the UI stay in agreement.
    /// </summary>
    [Fact]
    public void Api_EnablesPlanPersistence_SoThePlanSurfaceExists()
    {
        (_, string body) = BlockFor("ca-retailpulse-api");

        body.Should().MatchRegex(
            @"name:\s*'PlanPersistence__Enabled'\s*value:\s*'true'",
            "the API must enable plan persistence, or /api/plans/* is unmapped and the "
            + "Plan execution path silently falls back to the single-specialist route");
    }

    /// <summary>
    /// This environment is a full-capability demo. The human-in-the-loop plan review gate
    /// and durable session history are both opt-in and both default OFF, so they must be
    /// asserted explicitly or a future re-provision could quietly drop the demo back to a
    /// reduced surface.
    /// </summary>
    [Fact]
    public void Api_EnablesTheHumanApprovalGateAndSessionHistory()
    {
        (_, string body) = BlockFor("ca-retailpulse-api");

        body.Should().MatchRegex(
            @"name:\s*'PlanReview__Enabled'\s*value:\s*'true'",
            "the plan review approval gate must be on for the full demo");

        body.Should().MatchRegex(
            @"name:\s*'SessionPersistence__Enabled'\s*value:\s*'true'",
            "durable session history must be on for the full demo");
    }
}

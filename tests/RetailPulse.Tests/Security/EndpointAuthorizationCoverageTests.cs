using FluentAssertions;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Source-level guardrail ensuring no protected API endpoint group ships anonymous.
///
/// The API sets a strong <c>DefaultPolicy</c> but intentionally NOT a
/// <c>FallbackPolicy</c> (health/liveness must stay anonymous). That means an endpoint
/// mapped without an explicit <c>RequireAuthorization()</c> silently falls through to
/// anonymous — exactly the regression where <c>/api/memory</c> exposed per-user
/// conversation memory without a token. These tests scan the endpoint registration
/// sources so that class of mistake fails the build instead of shipping.
/// </summary>
public sealed class EndpointAuthorizationCoverageTests
{
    private static readonly string EndpointsDir = Path.Combine(
        FindRepoRoot(), "src", "RetailPulse.Api", "Endpoints");

    public static IEnumerable<object[]> EndpointFiles()
    {
        foreach (string path in Directory.EnumerateFiles(EndpointsDir, "*.cs"))
        {
            yield return new object[] { Path.GetFileName(path) };
        }
    }

    [Theory]
    [MemberData(nameof(EndpointFiles))]
    public void EveryEndpointGroup_RequiresAuthorization(string fileName)
    {
        string source = File.ReadAllText(Path.Combine(EndpointsDir, fileName));

        // Every endpoint-registration file maps protected, billable REST surfaces, so it
        // must assert authorization at least once (group-level or per-route). Health and
        // liveness are anonymous by design but live in ServiceDefaults, not here.
        source.Should().Contain("RequireAuthorization",
            $"{fileName} maps protected endpoints and must call RequireAuthorization so it " +
            "does not fall through to anonymous (the app sets DefaultPolicy, not FallbackPolicy)");
    }

    [Fact]
    public void EveryMappedRouteGroup_IsAuthorizedAtTheGroupLevel()
    {
        // A MapGroup that authorizes its routes individually is fine, but a group that
        // opens a protected route surface should carry a group-level RequireAuthorization
        // so newly added routes inherit the policy. Assert each MapGroup file authorizes
        // the group unless it authorizes every route explicitly.
        foreach (string path in Directory.EnumerateFiles(EndpointsDir, "*.cs"))
        {
            string source = File.ReadAllText(path);
            if (!source.Contains("MapGroup(", StringComparison.Ordinal))
            {
                continue;
            }

            source.Should().Contain("RequireAuthorization",
                $"{Path.GetFileName(path)} declares a route group and must authorize it");
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
            "Could not locate repo root (RetailPulse.slnx) walking up from " +
            AppContext.BaseDirectory);
    }
}

using System.Xml.Linq;
using FluentAssertions;

namespace RetailPulse.Tests.Deployment;

/// <summary>
/// Contract guardrail pinning the Microsoft Agent Framework (MAF) and
/// Microsoft.Extensions.AI package floors declared in
/// <c>Directory.Packages.props</c>. Any accidental downgrade below the versions
/// established by issue #88 fails CI before it can regress the rest of the
/// codebase.
///
/// Follows the file-inspection style of <see cref="DeploymentContractTests"/> —
/// this test never restores or executes packages, it only parses the central
/// package-management file.
/// </summary>
public class MafPackageVersionContractTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    // Floors set by issue #88 (Wave 0 MAF upgrade). Bumps are allowed; downgrades
    // are not — behavioural migration to newer MAF primitives happens in #89 and
    // beyond, and those follow-ups must not silently regress the floor.
    private static readonly (string PackageId, Version Floor)[] RequiredFloors =
    [
        ("Microsoft.Agents.AI", new Version(1, 18, 0)),
        ("Microsoft.Agents.AI.Abstractions", new Version(1, 18, 0)),
        ("Microsoft.Agents.AI.OpenAI", new Version(1, 18, 0)),
        ("Microsoft.Agents.AI.Workflows", new Version(1, 18, 0)),
        ("Microsoft.Extensions.AI", new Version(10, 9, 0)),
    ];

    [Theory]
    [InlineData("Microsoft.Agents.AI")]
    [InlineData("Microsoft.Agents.AI.Abstractions")]
    [InlineData("Microsoft.Agents.AI.OpenAI")]
    [InlineData("Microsoft.Agents.AI.Workflows")]
    [InlineData("Microsoft.Extensions.AI")]
    public void DirectoryPackagesProps_PinsPackageAtOrAboveFloor(string packageId)
    {
        Version floor = RequiredFloors.Single(f => f.PackageId == packageId).Floor;
        Dictionary<string, Version> pins = LoadCentralPackagePins();

        pins.Should().ContainKey(packageId,
            $"Directory.Packages.props must pin {packageId} centrally");

        pins[packageId].Should().BeGreaterThanOrEqualTo(floor,
            $"{packageId} must stay at or above {floor} (issue #88 floor); a lower version " +
            "is an accidental downgrade of the Microsoft Agent Framework upgrade");
    }

    [Fact]
    public void RetailPulseApi_ReferencesMicrosoftAgentsAiWorkflows()
    {
        // Issue #88 explicitly requires the API project to reference
        // Microsoft.Agents.AI.Workflows so the workflow primitives resolve
        // even before behavioural migration in #89 begins.
        string csprojPath = Path.Combine(RepoRoot, "src", "RetailPulse.Api", "RetailPulse.Api.csproj");
        File.Exists(csprojPath).Should().BeTrue("RetailPulse.Api.csproj must exist");

        var project = XDocument.Load(csprojPath);
        IEnumerable<string> referencedPackages = project
            .Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty);

        referencedPackages.Should().Contain("Microsoft.Agents.AI.Workflows",
            "RetailPulse.Api must reference Microsoft.Agents.AI.Workflows (issue #88)");
    }

    private static Dictionary<string, Version> LoadCentralPackagePins()
    {
        string path = Path.Combine(RepoRoot, "Directory.Packages.props");
        File.Exists(path).Should().BeTrue("Directory.Packages.props must exist at repo root");

        var doc = XDocument.Load(path);
        var pins = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);

        foreach (XElement element in doc.Descendants("PackageVersion"))
        {
            string? id = (string?)element.Attribute("Include");
            string? version = (string?)element.Attribute("Version");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            if (Version.TryParse(version, out Version? parsed))
            {
                pins[id] = parsed;
            }
        }

        return pins;
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

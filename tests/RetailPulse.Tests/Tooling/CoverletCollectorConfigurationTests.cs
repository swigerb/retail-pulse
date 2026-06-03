using System.Xml.Linq;
using FluentAssertions;

namespace RetailPulse.Tests.Tooling;

/// <summary>
/// Guardrails around the <c>coverlet.collector</c> wiring. These exist because
/// the package crossed a four-major-version boundary (6.x → 10.x) and the
/// CI coverage pipeline depends on it producing well-formed XPlat output.
///
/// The tests are intentionally static: they only inspect repo files
/// (Directory.Packages.props, the test .csproj, the CI workflow) and any
/// coverage artifacts that happen to be sitting in a TestResults directory.
/// They do <em>not</em> spawn a nested <c>dotnet test</c> run — that would
/// recurse. The end-to-end verification lives in
/// <c>tests/verify-coverage-collection.ps1</c>.
/// </summary>
public class CoverletCollectorConfigurationTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void CoverletCollector_IsPinnedInCentralPackageProps()
    {
        string propsPath = Path.Combine(RepoRoot, "Directory.Packages.props");
        File.Exists(propsPath).Should().BeTrue("central package manifest must exist");

        var doc = XDocument.Load(propsPath);
        XElement? coverlet = doc.Descendants("PackageVersion")
            .FirstOrDefault(e => string.Equals(
                e.Attribute("Include")?.Value,
                "coverlet.collector",
                StringComparison.Ordinal));

        coverlet.Should().NotBeNull(
            "coverlet.collector must remain centrally managed in Directory.Packages.props");

        string? versionAttr = coverlet?.Attribute("Version")?.Value;
        versionAttr.Should().NotBeNullOrWhiteSpace("coverlet.collector must have a pinned version");

        Version parsed = ParseVersion(versionAttr ?? string.Empty);

        // Floor matches the original (v6) baseline; ceiling is permissive so
        // future upgrades pass without churn. We just guard against an
        // accidental downgrade below the validated v6 baseline.
        parsed.Should().BeGreaterThanOrEqualTo(
            new Version(6, 0, 0),
            "downgrading coverlet.collector below v6 reintroduces older known issues");
    }

    [Fact]
    public void CoverletCollector_IsReferencedByPrimaryTestProject()
    {
        string csproj = Path.Combine(RepoRoot, "tests", "RetailPulse.Tests", "RetailPulse.Tests.csproj");
        File.Exists(csproj).Should().BeTrue();

        var doc = XDocument.Load(csproj);
        bool referenced = doc.Descendants("PackageReference")
            .Any(e => string.Equals(
                e.Attribute("Include")?.Value,
                "coverlet.collector",
                StringComparison.Ordinal));

        referenced.Should().BeTrue(
            "the main test project must reference coverlet.collector so " +
            "`dotnet test --collect:\"XPlat Code Coverage\"` produces output");
    }

    [Fact]
    public void CiWorkflow_StillCollectsXPlatCoverage()
    {
        string ci = Path.Combine(RepoRoot, ".github", "workflows", "ci.yml");
        File.Exists(ci).Should().BeTrue("CI workflow must exist");

        string text = File.ReadAllText(ci);
        text.Should().Contain("--collect:\"XPlat Code Coverage\"",
            "CI relies on XPlat collection — the upgrade must not break the invocation shape");
        text.Should().Contain("coverage.opencover.xml",
            "CI uploads the opencover artifact; the format override must still be honored");
    }

    /// <summary>
    /// If a previous local or CI run dropped a coverage file in TestResults,
    /// validate that the schema still matches what ReportGenerator (and
    /// downstream consumers) expect. When no file is present, the assertion
    /// is skipped — this test is opportunistic, not gating.
    /// </summary>
    [Fact]
    public void GeneratedCoverageArtifact_IfPresent_HasValidShape()
    {
        string testResults = Path.Combine(RepoRoot, "tests", "RetailPulse.Tests", "TestResults");
        if (!Directory.Exists(testResults))
        {
            // No prior run — nothing to validate. The PS1 script is the
            // authoritative end-to-end check.
            return;
        }

        string[] coberturaFiles = Directory.GetFiles(
            testResults, "coverage.cobertura.xml", SearchOption.AllDirectories);
        string[] openCoverFiles = Directory.GetFiles(
            testResults, "coverage.opencover.xml", SearchOption.AllDirectories);

        if (coberturaFiles.Length == 0 && openCoverFiles.Length == 0)
        {
            return; // Nothing to validate.
        }

        if (coberturaFiles.Length > 0)
        {
            AssertCoberturaShape(coberturaFiles[0]);
        }

        if (openCoverFiles.Length > 0)
        {
            AssertOpenCoverShape(openCoverFiles[0]);
        }
    }

    private static void AssertCoberturaShape(string path)
    {
        var doc = XDocument.Load(path);
        XElement? root = doc.Root;

        root.Should().NotBeNull();
        root?.Name.LocalName.Should().Be("coverage",
            "Cobertura reports start with a <coverage> root");

        root?.Attribute("line-rate").Should().NotBeNull("Cobertura must report line-rate");
        root?.Attribute("branch-rate").Should().NotBeNull("Cobertura must report branch-rate");

        int packageCount = root?.Descendants("package").Count() ?? 0;
        int classCount = root?.Descendants("class").Count() ?? 0;
        int methodCount = root?.Descendants("method").Count() ?? 0;

        packageCount.Should().BeGreaterThan(0, "at least one package should be covered");
        classCount.Should().BeGreaterThan(0, "at least one class should be covered");
        methodCount.Should().BeGreaterThan(0, "at least one method should be covered");
    }

    private static void AssertOpenCoverShape(string path)
    {
        var doc = XDocument.Load(path);
        XElement? root = doc.Root;

        root.Should().NotBeNull();
        root?.Name.LocalName.Should().Be("CoverageSession",
            "OpenCover reports start with <CoverageSession>");

        XElement? summary = root?.Element("Summary");
        summary.Should().NotBeNull("OpenCover must include a top-level <Summary>");
        summary?.Attribute("numClasses").Should().NotBeNull();
        summary?.Attribute("numMethods").Should().NotBeNull();
    }

    private static Version ParseVersion(string raw)
    {
        // Strip any pre-release / build suffix (e.g. "10.0.1-preview.1+sha").
        int dashIdx = raw.IndexOfAny(['-', '+']);
        string trimmed = dashIdx >= 0 ? raw[..dashIdx] : raw;

        // Version.Parse requires at least Major.Minor. Pad if needed.
        if (!trimmed.Contains('.'))
        {
            trimmed += ".0";
        }

        return Version.Parse(trimmed);
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

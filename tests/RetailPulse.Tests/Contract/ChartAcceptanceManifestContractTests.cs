using System.Text.RegularExpressions;
using FluentAssertions;
using RetailPulse.Contracts.Charts;
using Xunit;

namespace RetailPulse.Tests.Contract;

/// <summary>
/// Cross-language contract test for the curated chart acceptance manifest.
///
/// The single source of truth for the curated prompt library is the frontend file
/// <c>src/RetailPulse.Web/src/constants/prompts.ts</c>. The backend acceptance manifest
/// <see cref="ChartAcceptanceManifest"/> and the frontend mirror
/// <c>src/RetailPulse.Web/src/components/chartAcceptance.ts</c> must both cover exactly
/// the "Charts" category prompts (in prompt-source order) plus the previously-validated
/// two-brand QSR comparison prompt — and the README's user-facing chart bullet list must
/// stay synchronized with that surface.
///
/// This test parses <c>prompts.ts</c> and <c>README.md</c> directly and asserts:
///   * every "Charts" prompt appears in the manifest, in source order,
///   * the QSR comparison prompt appears at the tail of the manifest,
///   * every manifest prompt is present in the README's chart bullet list,
///   * the manifest has no orphan entries that are absent from the prompt source.
/// A drift in any of those three surfaces fails CI immediately.
/// </summary>
public sealed partial class ChartAcceptanceManifestContractTests
{
    private const string ChartsCategoryId = "charts";
    private const string QsrCategoryId = "qsr";
    private const string QsrComparisonPromptPrefix = "Compare Coastline Tacos vs Apex Grill";

    [Fact]
    public void Manifest_MatchesFrontendPromptSource_ChartsCategoryPlusQsrComparison()
    {
        IReadOnlyList<string> chartPrompts = ReadPromptCategory(ChartsCategoryId);
        IReadOnlyList<string> qsrPrompts = ReadPromptCategory(QsrCategoryId);
        string? comparisonPrompt = qsrPrompts.SingleOrDefault(p => p.StartsWith(QsrComparisonPromptPrefix, StringComparison.Ordinal));
        comparisonPrompt.Should().NotBeNull("the QSR two-brand comparison prompt must exist in the prompt source");

        var expected = new List<string>(chartPrompts) { comparisonPrompt };

        ChartAcceptanceManifest.Prompts.Should().Equal(
            expected,
            "the acceptance manifest is the backend mirror of the Charts category (in source order) "
            + "plus the two-brand QSR comparison prompt — any drift breaks the cross-language contract");
    }

    [Fact]
    public void Manifest_PromptsAllAppearInReadmeChartExamples()
    {
        HashSet<string> readmePrompts = ReadReadmeChartBulletPrompts();

        foreach (string prompt in ChartAcceptanceManifest.Prompts)
        {
            if (prompt.StartsWith(QsrComparisonPromptPrefix, StringComparison.Ordinal))
            {
                // The QSR two-brand comparison is documented in a separate example block
                // (the comparison-chart P0 fix in issue #32) rather than the Charts bullet
                // list, so it's not expected in the Charts README bullets.
                continue;
            }

            readmePrompts.Should().Contain(
                prompt,
                $"README chart bullet list must include the curated Charts prompt '{prompt}'");
        }
    }

    [Fact]
    public void Manifest_EveryCaseHasCoherentSemantics()
    {
        foreach (ChartAcceptanceCase c in ChartAcceptanceManifest.Cases)
        {
            c.Prompt.Should().NotBeNullOrWhiteSpace();
            c.ChartType.Should().NotBeNullOrWhiteSpace();
            c.RoutedIntent.Should().NotBeNullOrWhiteSpace();
            c.RoutedIntent.Should().NotBe(
                "PortfolioHealth",
                "explicit chart prompts must never route to the multi-agent health council");
            c.MinSeries.Should().BeGreaterThanOrEqualTo(1);
            c.MinMarks.Should().BeGreaterThanOrEqualTo(1);
            c.AxisUnit.Should().NotBeNullOrWhiteSpace();

            if (c.ChartType is "groupedBar" or "stackedBar")
            {
                c.MinSeries.Should().BeGreaterThanOrEqualTo(
                    2,
                    "a grouped/stacked chart is by definition multi-series");
            }
        }
    }

    private static IReadOnlyList<string> ReadPromptCategory(string categoryId)
    {
        string promptsPath = ResolveRepoRelativePath("src", "RetailPulse.Web", "src", "constants", "prompts.ts");
        string source = File.ReadAllText(promptsPath);

        // Locate the category block by id, then extract the enclosing prompts: […] array.
        Match idMatch = Regex.Match(source, $@"id:\s*'{Regex.Escape(categoryId)}'");
        idMatch.Success.Should().BeTrue($"category '{categoryId}' must exist in prompts.ts");

        int cursor = idMatch.Index;
        Match promptsHeader = MyRegex().Match(source[cursor..]);
        promptsHeader.Success.Should().BeTrue($"prompts array must follow id '{categoryId}'");
        int arrStart = cursor + promptsHeader.Index + promptsHeader.Length;

        int arrEnd = FindMatchingBracket(source, arrStart - 1);
        arrEnd.Should().BeGreaterThan(arrStart, $"prompts array for '{categoryId}' must be closed");

        string arrBody = source[arrStart..arrEnd];
        var extracted = new List<string>();
        foreach (Match m in SingleQuotedStringRegex().Matches(arrBody))
        {
            extracted.Add(Regex.Unescape(m.Groups[1].Value));
        }

        extracted.Should().NotBeEmpty($"category '{categoryId}' should have prompts");
        return extracted;
    }

    private static HashSet<string> ReadReadmeChartBulletPrompts()
    {
        string readmePath = ResolveRepoRelativePath("README.md");
        string readme = File.ReadAllText(readmePath);
        var results = new HashSet<string>(StringComparer.Ordinal);

        // README chart bullets are formatted:  - *"…"* → …
        foreach (Match m in ReadmeChartBulletRegex().Matches(readme))
        {
            results.Add(m.Groups[1].Value);
        }
        return results;
    }

    private static int FindMatchingBracket(string source, int openIndex)
    {
        int depth = 0;
        for (int i = openIndex; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static string ResolveRepoRelativePath(params string[] segments)
    {
        // Walk up from the test binary's directory until we find the repo root (contains
        // a README.md and a src/ folder). Works from any test host layout.
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12; i++)
        {
            if (File.Exists(Path.Combine(dir, "README.md"))
                && Directory.Exists(Path.Combine(dir, "src")))
            {
                return Path.Combine([dir, .. segments]);
            }
            string? parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir) break;
            dir = parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate repository root from test binary directory: " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"prompts:\s*\[")]
    private static partial Regex MyRegex();

    [GeneratedRegex(@"'([^'\\]*(?:\\.[^'\\]*)*)'")]
    private static partial Regex SingleQuotedStringRegex();

    [GeneratedRegex(@"^\s*[-*]\s+\*""([^""]+)""\*\s*(?:→|->)", RegexOptions.Multiline)]
    private static partial Regex ReadmeChartBulletRegex();
}

using System.Text.RegularExpressions;
using FluentAssertions;
using RetailPulse.Contracts.Charts;
using RetailPulse.Contracts.Prompts;
using Xunit;

namespace RetailPulse.Tests.Contract;

/// <summary>
/// Cross-language contract test for the curated PROSE prompt acceptance manifest.
///
/// The single source of truth for the curated prompt library is the frontend file
/// <c>src/RetailPulse.Web/src/constants/prompts.ts</c>. The backend prose manifest
/// <see cref="ProsePromptAcceptanceManifest"/> must cover exactly the non-chart
/// curated prompts in every non-chart category, in prompt-source order, and must
/// not carry orphans. The QSR two-brand chart-comparison prompt is intentionally
/// excluded because it is covered by <see cref="ChartAcceptanceManifest"/>.
///
/// This test parses <c>prompts.ts</c> directly (mirroring
/// <c>ChartAcceptanceManifestContractTests</c>) and asserts:
/// <list type="bullet">
///   <item>every non-chart prompt appears in the manifest, in source order,</item>
///   <item>the QSR two-brand chart-comparison prompt does NOT appear in the prose
///     manifest (it is chart-owned),</item>
///   <item>the manifest has no orphan entries that are absent from the prompt
///     source, and</item>
///   <item>every case has coherent semantics (non-empty prompt, expected intent,
///     expected agent key; never the council).</item>
/// </list>
/// A drift in any of these surfaces fails CI immediately, consistent with how
/// <c>ChartAcceptanceManifestContractTests</c> enforces the chart manifest.
/// </summary>
public sealed partial class ProsePromptAcceptanceManifestContractTests
{
    private const string ChartsCategoryId = "charts";
    private const string QsrCategoryId = "qsr";
    private const string QsrComparisonPromptPrefix = "Compare Coastline Tacos vs Apex Grill";

    [Fact]
    public void Manifest_MatchesFrontendPromptSource_EveryNonChartCategoryInSourceOrder()
    {
        IReadOnlyList<(string Id, string Label, IReadOnlyList<string> Prompts)> categories = ReadAllCategories();

        List<string> expectedProsePrompts = [];
        foreach ((string id, string _, IReadOnlyList<string> prompts) in categories)
        {
            if (string.Equals(id, ChartsCategoryId, StringComparison.Ordinal))
            {
                continue; // owned by ChartAcceptanceManifest
            }

            foreach (string prompt in prompts)
            {
                // The QSR two-brand comparison is chart-owned (implicitly promoted to a
                // grouped bar). Skip it here — chart manifest covers it.
                if (string.Equals(id, QsrCategoryId, StringComparison.Ordinal)
                    && prompt.StartsWith(QsrComparisonPromptPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                expectedProsePrompts.Add(prompt);
            }
        }

        ProsePromptAcceptanceManifest.Prompts.Should().Equal(
            expectedProsePrompts,
            "the prose acceptance manifest is the backend mirror of every non-chart curated "
            + "prompt in prompts.ts (in source order); any drift breaks the cross-language contract");
    }

    [Fact]
    public void Manifest_ExcludesTheQsrTwoBrandChartComparisonPrompt()
    {
        // Regression guard: the QSR "Compare Coastline Tacos vs Apex Grill …" prompt is a
        // chart-comparison prompt covered by ChartAcceptanceManifest. It must NOT appear
        // in the prose manifest — otherwise a live sweep would try to assert prose-only
        // response semantics against a prompt that legitimately produces a grouped bar.
        ProsePromptAcceptanceManifest.Prompts
            .Should().NotContain(
                p => p.StartsWith(QsrComparisonPromptPrefix, StringComparison.Ordinal),
                "the QSR two-brand chart comparison is chart-owned; it must not appear in the prose manifest");
    }

    [Fact]
    public void Manifest_CoveredCategoryIds_MatchAllNonChartCategoriesInPromptSource()
    {
        IReadOnlyList<(string Id, string Label, IReadOnlyList<string> Prompts)> categories = ReadAllCategories();

        string[] expectedIds = [.. categories
            .Where(c => !string.Equals(c.Id, ChartsCategoryId, StringComparison.Ordinal))
            .Select(c => c.Id)];

        ProsePromptAcceptanceManifest.CoveredCategoryIds.Should().Equal(
            expectedIds,
            "the manifest's declared covered-category list must mirror every non-chart "
            + "category id in prompts.ts (in source order)");
    }

    [Fact]
    public void Manifest_EveryCaseHasCoherentSemantics()
    {
        foreach (ProsePromptAcceptanceCase c in ProsePromptAcceptanceManifest.Cases)
        {
            c.Prompt.Should().NotBeNullOrWhiteSpace();
            c.CategoryId.Should().NotBeNullOrWhiteSpace();
            c.ExpectedIntent.Should().NotBeNullOrWhiteSpace();
            c.ExpectedAgentKey.Should().NotBeNullOrWhiteSpace();
            c.Rationale.Should().NotBeNullOrWhiteSpace();

            c.ExpectedIntent.Should().NotBe(
                Contracts.Routing.AgentIntent.PortfolioHealth,
                $"prose prompt '{c.Prompt}' must never route to the multi-agent council — the "
                + "council produces a slow, prose-only response with no specialist tool-loop, "
                + "which is the exact 'empty/underpowered response' symptom this manifest prevents");

            c.ExpectedAgentKey.Should().NotBe(
                "council",
                $"prose prompt '{c.Prompt}' must never target the council agent key");

            ProsePromptAcceptanceManifest.CoveredCategoryIds.Should().Contain(
                c.CategoryId,
                $"case '{c.Prompt}' references category '{c.CategoryId}' which is not in CoveredCategoryIds");
        }
    }

    [Fact]
    public void Manifest_EveryPromptIsUnique()
    {
        // Guard against copy-paste duplication that would silently double-run a prompt
        // in the live browser sweep while masking a missed category entry.
        var duplicates = ProsePromptAcceptanceManifest.Cases
            .GroupBy(c => c.Prompt, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        duplicates.Should().BeEmpty(
            "every prose acceptance case must reference a distinct curated prompt");
    }

    // ── prompts.ts parsing (mirrors ChartAcceptanceManifestContractTests) ─────

    private static IReadOnlyList<(string Id, string Label, IReadOnlyList<string> Prompts)> ReadAllCategories()
    {
        string source = ReadPromptsSource();
        var results = new List<(string, string, IReadOnlyList<string>)>();

        // Every prompt-category block is emitted by the `mirrorCategory(id, label,
        // emoji, order, [ [prompt, capability], ... ])` helper. Parse each block by
        // matching the helper signature up to the opening `[` of its fifth argument,
        // then extract the leading single-quoted string from every nested tuple
        // (that's the submitted prompt; the capability that follows is a helper call,
        // not a string literal, so it cannot confuse the leading-string match).
        foreach (Match blockMatch in MirrorCategoryBlockRegex().Matches(source))
        {
            string id = blockMatch.Groups["id"].Value;
            string label = Regex.Unescape(blockMatch.Groups["label"].Value);
            int arrStart = blockMatch.Index + blockMatch.Length;
            int arrEnd = FindMatchingBracket(source, arrStart - 1);
            if (arrEnd < 0) continue;

            string arrBody = source[arrStart..arrEnd];
            var prompts = new List<string>();
            foreach (Match m in TupleLeadingStringRegex().Matches(arrBody))
            {
                prompts.Add(Regex.Unescape(m.Groups[1].Value));
            }

            results.Add((id, label, prompts));
        }

        results.Should().NotBeEmpty("prompts.ts must contain at least one mirrorCategory block");
        return results;
    }

    private static string ReadPromptsSource()
    {
        string promptsPath = ResolveRepoRelativePath(
            "src", "RetailPulse.Web", "src", "constants", "prompts.ts");
        return File.ReadAllText(promptsPath);
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

    [GeneratedRegex(
        @"mirrorCategory\(\s*'(?<id>[a-z][a-z0-9-]*)'\s*,\s*'(?<label>[^'\\]*(?:\\.[^'\\]*)*)'\s*,\s*'[^']*'\s*,\s*\d+\s*,\s*\[")]
    private static partial Regex MirrorCategoryBlockRegex();

    [GeneratedRegex(@"\[\s*'([^'\\]*(?:\\.[^'\\]*)*)'")]
    private static partial Regex TupleLeadingStringRegex();
}

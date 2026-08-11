using System.Text.RegularExpressions;
using FluentAssertions;
using RetailPulse.Api.Charts;
using RetailPulse.Contracts.Charts;
using RetailPulse.Contracts.Routing;
using Xunit;

namespace RetailPulse.Tests.Contract;

/// <summary>
/// Independent production acceptance sweep across EVERY curated "Prompt ideas" entry.
///
/// The existing <see cref="ChartAcceptanceManifestContractTests"/> guards the 9-prompt
/// chart-acceptance matrix (Chick + Costco own the render/render-path suites). This suite
/// closes the acceptance gap for the full curated library — all 26 prompts across the
/// 7 tenant/domain-neutral categories exposed by the "Prompt ideas" popover in production:
///
///   • General Retail (3)  • Grocery (3)   • Quick-Serve Restaurants (3)
///   • Home Improvement (3) • Office Supply (3) • Furniture (3) • Charts (8)
///
/// For every entry, we assert the deterministic <see cref="ChartRequestDetector"/> classifies
/// it into the correct response class:
///   • CHART: the 8 Charts-category prompts (each with the expected chart type)
///     + the QSR two-brand comparison prompt (implicitly promoted → grouped bar)
///     is covered by <see cref="ChartAcceptanceManifest"/> and the render matrix.
///   • NON-CHART: the remaining 17 curated prompts (prose response). These must
///     NEVER be classified as an explicit chart request — misclassifying a
///     "How is X performing?" prompt as a chart request routes it into the chart
///     fulfillment path and produces the exact "Chart unavailable" P0 symptom
///     for a prompt that was only ever supposed to return prose.
///
/// This is deliberately NOT a rendering test (no seeded tools, no ChartSpec builder — those
/// paths are already covered by ChartAcceptanceMatrixTests). It is the classification
/// half of the production acceptance matrix: the invariant that "every curated prompt has
/// a fixed, contract-tested response class" — the gate the live browser sweep relies on.
/// </summary>
public sealed partial class ProductionPromptAcceptanceTests
{
    private const int ExpectedCategoryCount = 7;
    private const int ExpectedTotalPromptCount = 26;
    private const string ChartsCategoryId = "charts";
    private const string QsrCategoryId = "qsr";
    private const string QsrComparisonPromptPrefix = "Compare Coastline Tacos vs Apex Grill";

    /// <summary>
    /// Categories that the production "Prompt ideas" popover must expose (id → label).
    /// Guards against silent addition/removal of a tenant/domain tab.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ExpectedCategories =
        new Dictionary<string, string>
        {
            ["general"] = "General Retail",
            ["grocery"] = "Grocery",
            ["qsr"] = "Quick-Serve Restaurants",
            ["home-improvement"] = "Home Improvement",
            ["office-supply"] = "Office Supply",
            ["furniture"] = "Furniture",
            ["charts"] = "Charts",
        };

    [Fact]
    public void PromptLibrary_ExposesExpectedCategoriesAndCount()
    {
        IReadOnlyList<(string Id, string Label, IReadOnlyList<string> Prompts)> categories = ReadAllCategories();

        categories.Should().HaveCount(ExpectedCategoryCount,
            "the production Prompt ideas popover exposes a fixed set of curated categories");

        foreach ((string id, string label) in ExpectedCategories)
        {
            categories.Should().ContainSingle(c => c.Id == id,
                $"category '{id}' must exist in the production prompt library");
            categories.Single(c => c.Id == id).Label.Should().Be(label,
                $"category '{id}' must keep its production label");
        }

        int total = categories.Sum(c => c.Prompts.Count);
        total.Should().Be(ExpectedTotalPromptCount,
            "the production Prompt ideas popover exposes exactly {0} curated entries " +
            "(3 in each of the 6 domain categories + 8 in Charts); a drift here means the " +
            "acceptance matrix is stale and every downstream production sweep must be re-run.",
            ExpectedTotalPromptCount);
    }

    [Fact]
    public void EveryChartsCategoryPrompt_IsClassifiedAsExplicitChartRequest_WithExpectedType()
    {
        IReadOnlyList<string> chartsPrompts = ReadPromptCategory(ChartsCategoryId);
        chartsPrompts.Should().HaveCount(8, "the production Charts tab exposes 8 curated chart prompts");

        // Every Charts prompt must ALSO appear in the acceptance manifest with the same
        // chart type. This is the classification half of the manifest's semantic contract.
        var manifestByPrompt = ChartAcceptanceManifest.Cases
            .ToDictionary(c => c.Prompt, c => c.ChartType, StringComparer.Ordinal);

        foreach (string prompt in chartsPrompts)
        {
            ChartIntent intent = ChartRequestDetector.Detect(prompt);

            intent.IsExplicitChartRequest.Should().BeTrue(
                $"Charts-category prompt '{prompt}' must be classified as an explicit chart request; " +
                "misclassifying it as prose would strand the request in the general council path " +
                "and produce the #50 P0 'no chart rendered' symptom.");

            intent.RoutedIntent.Should().NotBe(
                AgentIntent.PortfolioHealth,
                "explicit chart prompts must never route to the multi-agent health council");

            manifestByPrompt.Should().ContainKey(prompt,
                $"acceptance manifest must cover Charts prompt '{prompt}' (chart-render acceptance guarantee)");

            string manifestType = manifestByPrompt[prompt];
            intent.ChartType.Should().Be(manifestType,
                $"detector-inferred chart type must match the manifest for prompt '{prompt}'");
        }
    }

    [Fact]
    public void QsrTwoBrandComparisonPrompt_IsCoveredByAcceptanceManifest()
    {
        IReadOnlyList<string> qsrPrompts = ReadPromptCategory(QsrCategoryId);
        string? qsrComparison = qsrPrompts.SingleOrDefault(
            p => p.StartsWith(QsrComparisonPromptPrefix, StringComparison.Ordinal));

        qsrComparison.Should().NotBeNull(
            "the QSR two-brand comparison prompt must exist in the prompt source");

        ChartAcceptanceManifest.Cases.Should().ContainSingle(c => c.Prompt == qsrComparison,
            "the QSR two-brand comparison prompt must have a chart acceptance case " +
            "(it is implicitly promoted to a grouped bar via the compare-two-brands fulfillment path)");

        // The QSR comparison prompt has no chart noun, so it deliberately is NOT an
        // "explicit" chart request — the render acceptance is provided by the
        // implicit-comparison fulfillment path exercised in ChartAcceptanceMatrixTests.
        // Assert the detector is honest about that (it must not fire on ordinary
        // "Compare A vs B" prose or the general specialist would lose the request).
        ChartIntent intent = ChartRequestDetector.Detect(qsrComparison);
        intent.IsExplicitChartRequest.Should().BeFalse(
            "the QSR comparison prompt has no chart noun/type — it must route via the " +
            "implicit two-brand comparison path, not the explicit chart detector");
    }

    [Fact]
    public void EveryNonChartCuratedPrompt_IsClassifiedAsProseResponse()
    {
        // Every prompt outside the Charts category (except the QSR two-brand comparison
        // which is covered above) must be classified as a NON-chart request. If any of
        // these accidentally trips the explicit-chart detector, the router will hand it
        // to a chart specialist and the model will be forced into the chart path — the
        // exact regression that #50 was opened to prevent.
        IReadOnlyList<(string Id, string Label, IReadOnlyList<string> Prompts)> categories = ReadAllCategories();

        var nonChartPrompts = categories
            .Where(c => c.Id != ChartsCategoryId)
            .SelectMany(c => c.Prompts)
            .Where(p => !p.StartsWith(QsrComparisonPromptPrefix, StringComparison.Ordinal))
            .ToList();

        nonChartPrompts.Should().HaveCount(17,
            "6 domain categories × 3 prompts each = 18, minus the 1 QSR two-brand " +
            "comparison prompt that IS a chart case = 17 pure-prose curated entries");

        foreach (string prompt in nonChartPrompts)
        {
            ChartIntent intent = ChartRequestDetector.Detect(prompt);
            intent.IsExplicitChartRequest.Should().BeFalse(
                $"non-chart curated prompt '{prompt}' must NOT be classified as an " +
                "explicit chart request — otherwise the router forces the chart-fulfillment " +
                "path on a prose prompt and the user sees a 'Chart unavailable' diagnostic " +
                "instead of the expected narrative answer.");
            intent.ChartType.Should().BeNull(
                $"non-chart prompt '{prompt}' must not carry a chart-type hint");
        }
    }

    // ─── prompts.ts parsing (mirrors ChartAcceptanceManifestContractTests) ────────────

    private static IReadOnlyList<(string Id, string Label, IReadOnlyList<string> Prompts)> ReadAllCategories()
    {
        string source = ReadPromptsSource();
        var results = new List<(string, string, IReadOnlyList<string>)>();

        foreach (Match idMatch in CategoryIdRegex().Matches(source))
        {
            string id = idMatch.Groups[1].Value;
            int cursor = idMatch.Index;

            Match labelMatch = LabelRegex().Match(source[cursor..]);
            string label = labelMatch.Success ? Regex.Unescape(labelMatch.Groups[1].Value) : "";

            Match promptsHeader = PromptsHeaderRegex().Match(source[cursor..]);
            if (!promptsHeader.Success) continue;
            int arrStart = cursor + promptsHeader.Index + promptsHeader.Length;
            int arrEnd = FindMatchingBracket(source, arrStart - 1);
            if (arrEnd < 0) continue;

            string arrBody = source[arrStart..arrEnd];
            var prompts = new List<string>();
            foreach (Match m in SingleQuotedStringRegex().Matches(arrBody))
            {
                prompts.Add(Regex.Unescape(m.Groups[1].Value));
            }

            results.Add((id, label, prompts));
        }

        return results;
    }

    private static IReadOnlyList<string> ReadPromptCategory(string categoryId)
        => ReadAllCategories().Single(c => c.Id == categoryId).Prompts;

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

    [GeneratedRegex(@"id:\s*'([a-z][a-z0-9-]*)'")]
    private static partial Regex CategoryIdRegex();

    [GeneratedRegex(@"label:\s*'([^'\\]*(?:\\.[^'\\]*)*)'")]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"prompts:\s*\[")]
    private static partial Regex PromptsHeaderRegex();

    [GeneratedRegex(@"'([^'\\]*(?:\\.[^'\\]*)*)'")]
    private static partial Regex SingleQuotedStringRegex();
}

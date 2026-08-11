using FluentAssertions;
using RetailPulse.Api.Charts;
using RetailPulse.Contracts.Charts;
using RetailPulse.Contracts.Routing;
using Xunit;

namespace RetailPulse.Tests.Charts;

/// <summary>
/// Regression tests for the <see cref="ChartPromptCatalog"/> / detector integration
/// added to close both #76 blockers:
///
///   * BLOCKER 2 — Publix prompt #8 ("Compare Coastline Tacos vs Apex Grill
///     depletions across all regions") is a chart prompt on the acceptance
///     manifest but is not linguistically recognised as one (no chart-type
///     word, no chart noun — just "Compare X vs Y"). Prior to this fix the
///     drop-on-prose invariant (61c7e90) dropped every chart the model tried
///     to emit for it. The manifest-first override in the detector must
///     classify it as an explicit chart request with the manifest's canonical
///     type and routed intent.
///
///   * BLOCKER 1 — chart-intent classification for every curated chart prompt
///     must be a deterministic table lookup, not a heuristic. The classification
///     must be identical across arbitrary invocation orderings, case/whitespace
///     variance, and independent of any state.
///
/// The tests fail on the current branch head at 38b219c (no catalog) and pass
/// with the ChartPromptCatalog + detector integration.
/// </summary>
public sealed class ChartPromptCatalogDetectorTests
{
    /// <summary>
    /// BLOCKER 2 anchor. The exact prompt from ChartAcceptanceManifest whose
    /// linguistic form is a prose "Compare X vs Y" but whose curated intent is
    /// a groupedBar chart routed to DemandForecasting. Prior to the catalog
    /// integration the linguistic detector returns IsExplicitChartRequest=false
    /// for this text (no chart noun, no chart-type word), and the Group A
    /// drop-on-prose invariant then drops any chart the model produces.
    /// </summary>
    [Fact]
    public void Detect_ManifestChartPromptWithoutChartNoun_ClassifiedAsExplicitChart()
    {
        const string prompt = "Compare Coastline Tacos vs Apex Grill depletions across all regions";

        ChartIntent intent = ChartRequestDetector.Detect(prompt);

        intent.IsExplicitChartRequest.Should().BeTrue(
            "the acceptance manifest is the tenant-authoritative list of curated chart prompts — " +
            "a 'Compare X vs Y' prompt that lives on the manifest must not be classified as prose " +
            "just because it happens to lack a chart-type keyword (issue #76 BLOCKER 2)");
        intent.ChartType.Should().Be("groupedBar");
        intent.RoutedIntent.Should().Be(AgentIntent.DemandForecasting);
    }

    /// <summary>
    /// BLOCKER 1 gate: chart-intent classification for every curated chart prompt
    /// must be a run-invariant table lookup. We assert every acceptance-manifest
    /// prompt produces IsExplicitChartRequest=true with the manifest's canonical
    /// ChartType and RoutedIntent, and that the result is bit-identical across
    /// repeated invocations, whitespace variance, and letter-case variance.
    /// </summary>
    [Fact]
    public void Detect_EveryManifestPrompt_IsClassifiedFromManifest_Deterministically()
    {
        foreach (ChartAcceptanceCase c in ChartAcceptanceManifest.Cases)
        {
            var seenType = new HashSet<string?>();
            var seenIntent = new HashSet<string>();
            var seenExplicit = new HashSet<bool>();

            // Invoke repeatedly, and also with whitespace/case perturbations.
            // Every result must collapse to a single value: table-driven.
            string[] variants =
            [
                c.Prompt,
                c.Prompt,
                "  " + c.Prompt + "  ",
                c.Prompt.ToUpperInvariant(),
                c.Prompt.ToLowerInvariant(),
                c.Prompt.Replace(" ", "  "),
            ];

            foreach (string v in variants)
            {
                ChartIntent got = ChartRequestDetector.Detect(v);
                seenType.Add(got.ChartType);
                seenIntent.Add(got.RoutedIntent);
                seenExplicit.Add(got.IsExplicitChartRequest);
            }

            seenExplicit.Should().ContainSingle().Which.Should().BeTrue(
                $"chart-intent for manifest prompt '{c.Prompt}' must be a deterministic table lookup, " +
                "not a heuristic that varies with whitespace/case (issue #76 BLOCKER 1)");
            seenType.Should().ContainSingle().Which.Should().Be(c.ChartType,
                $"chart-type for manifest prompt '{c.Prompt}' must match the manifest exactly and be " +
                "run-invariant");
            seenIntent.Should().ContainSingle().Which.Should().Be(c.RoutedIntent,
                $"routed intent for manifest prompt '{c.Prompt}' must match the manifest exactly and be " +
                "run-invariant");
        }
    }

    /// <summary>
    /// The manifest override must NEVER cause a prose (non-manifest) prompt to
    /// be classified as an explicit chart request. We assert Group A's five
    /// sweep prose prompts still fall to <c>false</c>, preserving the invariant
    /// that saved 18 PROSE prompts in the Publix sweep.
    /// </summary>
    [Theory]
    [InlineData("Compare depletion trends across all regions for this quarter")]
    [InlineData("Compare Harvest Table vs FreshMart sell-through rates by region")]
    [InlineData("Compare ClearDesk Technology vs Paper Products sell-through by region")]
    [InlineData("Show me Urban Living depletion trends across all regions this quarter")]
    [InlineData("Compare Foundry Home vs Urban Living performance in the West Coast")]
    public void Detect_ProsePromptsFromSweep_StillClassifiedAsProse(string prosePrompt)
    {
        ChartIntent intent = ChartRequestDetector.Detect(prosePrompt);
        intent.IsExplicitChartRequest.Should().BeFalse(
            "the manifest override must not leak chart intent onto non-manifest prose asks — " +
            "the 18-PROSE-prompt gate from the Group A fix (61c7e90) must be preserved");
    }

    /// <summary>
    /// Catalog TryMatch is exact (canonicalized) — a superstring or a rewording
    /// must NOT match. This prevents accidental brand-hardcoded semantics from
    /// leaking through if someone extends the manifest carelessly.
    /// </summary>
    [Theory]
    [InlineData("Please: Compare Coastline Tacos vs Apex Grill depletions across all regions, thanks!")]
    [InlineData("Compare Coastline Tacos and Apex Grill depletions across all regions")]
    public void Catalog_TryMatch_IsExactAfterCanonicalization(string almostPrompt)
    {
        bool matched = ChartPromptCatalog.TryMatch(almostPrompt, out ChartAcceptanceCase? c);
        matched.Should().BeFalse();
        c.Should().BeNull();
    }
}

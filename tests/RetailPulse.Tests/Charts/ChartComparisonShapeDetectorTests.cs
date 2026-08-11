using FluentAssertions;
using RetailPulse.Api.Charts;
using RetailPulse.Contracts.Routing;
using Xunit;

namespace RetailPulse.Tests.Charts;

/// <summary>
/// Regression coverage for the generic comparison-shape chart-intent recognizer
/// added to close issue #76 (rejection of the earlier manifest-first override).
///
/// The recognizer classifies bare "Compare &lt;entity&gt; vs|and &lt;entity&gt;
/// &lt;metric&gt; [across|by &lt;scope&gt;]" sentences as chart-intent even when
/// they carry no chart/graph/plot noun, using only the domain-cue vocabulary the
/// router already carries — NEVER prompt-text or brand literals. These tests
/// therefore sample NON-manifest prompts: paraphrases of the manifest sentence,
/// "vs"/"and" swaps, politeness prefixes and suffixes, reordered scope clauses,
/// and DIFFERENT tenant brands not present in the acceptance manifest. All must
/// classify identically and correctly, proving the classifier is genuinely
/// tenant-generic and not "teaching to the test".
/// </summary>
public sealed class ChartComparisonShapeDetectorTests
{
    // Paraphrases of manifest prompt #8 ("Compare Coastline Tacos vs Apex Grill
    // depletions across all regions"). None of these strings is on the manifest.
    // Every one must classify as a chart request routed to DemandForecasting.
    [Theory]
    [InlineData("Compare Coastline Tacos and Apex Grill depletions across all regions")]
    [InlineData("Compare Coastline Tacos versus Apex Grill depletions across all regions")]
    [InlineData("Please compare Coastline Tacos vs Apex Grill depletions across all regions, thanks!")]
    [InlineData("Could you compare Apex Grill and Coastline Tacos depletions across all regions?")]
    [InlineData("Compare Coastline Tacos vs Apex Grill depletions by region")]
    [InlineData("Across all regions, compare Coastline Tacos vs Apex Grill depletions")]
    [InlineData("Contrast Coastline Tacos vs Apex Grill depletion volumes across all regions")]
    [InlineData("Comparison of Coastline Tacos and Apex Grill depletions by region")]
    public void Detect_ManifestPrompt8Paraphrases_AllClassifyAsChart(string prompt)
    {
        ChartIntent intent = ChartRequestDetector.Detect(prompt);

        intent.IsExplicitChartRequest.Should().BeTrue(
            $"paraphrase '{prompt}' has the same Compare-X-vs-Y-metric shape as manifest prompt #8 — " +
            "a genuinely generic classifier must not depend on the byte-identical curated string");
        intent.ChartType.Should().Be("groupedBar");
        intent.RoutedIntent.Should().Be(AgentIntent.DemandForecasting);
    }

    // Brands that DO NOT appear in the acceptance manifest. If the classifier were
    // "teaching to the test" via an allow-list keyed on manifest strings, none of
    // these would match — they'd fall through to prose and drop any emitted chart.
    // A generic recognizer must classify all of them as chart requests based on
    // sentence shape + metric vocabulary alone.
    [Theory]
    [InlineData("Compare Northwind Foods vs Contoso Grocery depletions across all regions", AgentIntent.DemandForecasting)]
    [InlineData("Compare Fabrikam Beverages and Adventure Works Cellars velocity by region", AgentIntent.DemandForecasting)]
    [InlineData("Compare Lucerne Publishing vs Blue Yonder Airlines market share across all regions", AgentIntent.CompetitiveMarket)]
    [InlineData("Contrast Trey Research and Wingtip Toys inventory across all regions", AgentIntent.SupplyShipments)]
    [InlineData("Comparison of Proseware and Litware shipment volumes by region", AgentIntent.SupplyShipments)]
    public void Detect_NonManifestBrands_AllClassifyAsChartByShapeAndMetric(string prompt, string expectedIntent)
    {
        ChartIntent intent = ChartRequestDetector.Detect(prompt);

        intent.IsExplicitChartRequest.Should().BeTrue(
            $"'{prompt}' names brands that are NOT on the acceptance manifest — the classifier " +
            "must recognise chart intent from sentence shape and metric vocabulary, not from a " +
            "hardcoded prompt allow-list");
        intent.ChartType.Should().Be("groupedBar");
        intent.RoutedIntent.Should().Be(expectedIntent,
            "the routed specialist must fall out of the metric vocabulary in the sentence, so the " +
            "same generic path handles every domain (depletion, share, inventory, …)");
    }

    // The comparison-shape recognizer must NOT fire on soft/narrative payloads:
    // "trends", "rates", "performance", "sentiment", etc. carry an aggregate
    // that a written analysis answers, not a chart. These are the exact prose
    // prompts flagged in the #76 sweep failure taxonomy Group A that still
    // classify as prose after the rewrite.
    [Theory]
    [InlineData("Compare depletion trends across all regions for this quarter")]
    [InlineData("Compare Harvest Table vs FreshMart sell-through rates by region")]
    [InlineData("Show me Urban Living depletion trends across all regions this quarter")]
    [InlineData("Compare Foundry Home vs Urban Living performance in the West Coast")]
    [InlineData("Compare Northwind Foods vs Contoso Grocery sell-through rates by region")]
    [InlineData("Compare Northwind Foods and Contoso Grocery depletion trends across all regions")]
    [InlineData("Contrast Fabrikam Beverages vs Adventure Works Cellars performance by region")]
    public void Detect_ComparisonWithSoftPayloadNoun_StaysProse(string prosePrompt)
    {
        ChartIntent intent = ChartRequestDetector.Detect(prosePrompt);

        intent.IsExplicitChartRequest.Should().BeFalse(
            $"'{prosePrompt}' asks for a NARRATIVE aggregate (trends/rates/performance) — a " +
            "generic classifier must keep these prose regardless of which tenant brands are named");
    }

    // Determinism: the classifier's output for a given prompt is a pure function
    // of the prompt text. Repeat, whitespace, and case perturbations — plus
    // syntactic reorderings that preserve meaning — must all collapse to the same
    // classification. This replaces the earlier "iterate the manifest and assert
    // it matches the manifest" tautology with a determinism gate that samples
    // NON-manifest prompts.
    [Theory]
    [InlineData("Compare Coastline Tacos vs Apex Grill depletions across all regions")]
    [InlineData("Compare Northwind Foods and Contoso Grocery depletions by region")]
    [InlineData("Contrast Fabrikam Beverages vs Adventure Works Cellars velocity across all regions")]
    public void Detect_NonManifestPrompt_IsDeterministicAcrossPerturbations(string prompt)
    {
        var seenExplicit = new HashSet<bool>();
        var seenType = new HashSet<string?>();
        var seenIntent = new HashSet<string>();

        string[] variants =
        [
            prompt,
            prompt,
            "  " + prompt + "  ",
            prompt.ToUpperInvariant(),
            prompt.ToLowerInvariant(),
            prompt.Replace(" ", "  "),
        ];

        foreach (string v in variants)
        {
            ChartIntent got = ChartRequestDetector.Detect(v);
            seenExplicit.Add(got.IsExplicitChartRequest);
            seenType.Add(got.ChartType);
            seenIntent.Add(got.RoutedIntent);
        }

        seenExplicit.Should().ContainSingle().Which.Should().BeTrue();
        seenType.Should().ContainSingle().Which.Should().Be("groupedBar");
        seenIntent.Should().ContainSingle();
    }

    // A chart-type word later in the sentence must still override the default
    // "groupedBar" from the comparison-shape recognizer, so users who DO name a
    // specific type get what they asked for.
    [Fact]
    public void Detect_ComparisonWithExplicitChartType_KeepsExplicitType()
    {
        ChartIntent intent = ChartRequestDetector.Detect(
            "Compare Northwind Foods vs Contoso Grocery depletions across all regions as a line chart");

        intent.IsExplicitChartRequest.Should().BeTrue();
        intent.ChartType.Should().Be("line");
    }
}

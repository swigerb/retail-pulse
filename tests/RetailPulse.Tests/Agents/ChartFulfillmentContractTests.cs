using FluentAssertions;
using RetailPulse.Api.Charts;
using RetailPulse.Contracts.Charts;
using RetailPulse.Contracts.Routing;
using Xunit;

namespace RetailPulse.Tests.Agents;

/// <summary>
/// Regression coverage for issue #74 — the P0 assistant prose must never contain
/// the banned "truncated / placeholder zeros / should not be used / chart shell"
/// fallback narrative that convinced the frontend that a zero-mark card was the
/// intended output. This suite pins:
///  * <see cref="ChartRequestDetector"/> classifies the exact P0 phrase as an
///    explicit horizontal-bar request routed to the aggregate-capable General
///    intent (not DemandForecasting);
///  * the chart-unavailable diagnostic emitted when data is missing does NOT
///    contain any banned phrase.
/// </summary>
public sealed class ChartFulfillmentContractTests
{
    private const string P0Prompt =
        "Show a horizontal bar chart ranking all brands by depletion growth rate";

    [Fact]
    public void Detect_TreatsEveryCuratedChartManifestPrompt_AsAnExplicitChartRequest()
    {
        // The manifest is the authoritative list of prompts that MUST yield a chart.
        // Not all of them name a chart — "Compare Coastline Tacos vs Apex Grill
        // depletions across all regions" has no chart noun — so the grammar rules alone
        // classified it as prose and the #76 Group A invariant dropped its chart.
        foreach (ChartAcceptanceCase c in ChartAcceptanceManifest.Cases)
        {
            ChartIntent intent = ChartRequestDetector.Detect(c.Prompt);

            intent.IsExplicitChartRequest.Should().BeTrue(
                "curated manifest prompt '{0}' must be treated as an explicit chart request", c.Prompt);
            intent.ChartType.Should().Be(c.ChartType,
                "the manifest declares the chart type for '{0}'", c.Prompt);
            intent.MinMarks.Should().Be(c.MinMarks,
                "the manifest's acceptance floor must reach the live pipeline for '{0}'", c.Prompt);
        }
    }

    [Fact]
    public void Detect_CuratedMatch_StillRoutesThroughTheDomainCues_NotTheManifest()
    {
        // The manifest's RoutedIntent is documentation and has drifted: it still lists
        // DemandForecasting for the P0 ranking prompt, which #74 re-routed to General so
        // the portfolio aggregate tool is reachable. Routing must stay with the cues.
        ChartIntent intent = ChartRequestDetector.Detect(P0Prompt);
        intent.RoutedIntent.Should().Be(AgentIntent.General);
    }

    [Fact]
    public void Detect_DoesNotTreatTheGroceryProseComparison_AsAChartRequest()
    {
        // Near-identical shape to the QSR chart comparison, but it lives in the PROSE
        // manifest. The manifest match must stay exact so it cannot bleed across.
        ChartIntent intent = ChartRequestDetector.Detect(
            "Compare Harvest Table vs FreshMart sell-through rates by region");

        intent.IsExplicitChartRequest.Should().BeFalse(
            "this prompt is prose — only curated chart-manifest entries are forced to charts");
    }

    [Fact]
    public void Detect_MatchesCuratedPrompts_IgnoringCaseWhitespaceAndTrailingPunctuation()
    {
        string prompt = ChartAcceptanceManifest.Cases[0].Prompt;
        string scruffy = "  " + prompt.ToUpperInvariant().Replace(" ", "   ") + ".  ";

        ChartRequestDetector.Detect(scruffy).IsExplicitChartRequest.Should().BeTrue();
    }

    [Fact]
    public void ChartRequestDetector_ExactP0Phrase_IsHorizontalBar_RoutedToGeneral()
    {
        ChartIntent intent = ChartRequestDetector.Detect(P0Prompt);

        intent.IsExplicitChartRequest.Should().BeTrue();
        intent.ChartType.Should().Be("horizontalBar");
        intent.RoutedIntent.Should().Be(AgentIntent.General,
            "ranking / growth-rate cues must be evaluated before the 'depletion' cue");
        intent.RoutedIntent.Should().NotBe(AgentIntent.DemandForecasting);
    }

    [Theory]
    [InlineData("rank all brands by growth rate as a horizontal bar chart", AgentIntent.General)]
    [InlineData("horizontal bar chart of top brands by depletion growth", AgentIntent.General)]
    [InlineData("bar chart of the fastest growing brands", AgentIntent.General)]
    [InlineData("horizontal bar chart comparing all brands by YoY growth", AgentIntent.General)]
    // Bare velocity phrasing still routes to demand.
    [InlineData("bar chart comparing depletion velocity for all spirits brands", AgentIntent.DemandForecasting)]
    public void ChartRequestDetector_Routes_Correctly(string message, string expectedIntent) => ChartRequestDetector.Detect(message).RoutedIntent.Should().Be(expectedIntent);

    [Fact]
    public void ChartUnavailableDiagnostic_ContainsNoBannedFallbackPhrases()
    {
        // Invoke the internal diagnostic via reflection to keep the test hermetic.
        System.Reflection.MethodInfo? m = typeof(Api.Agents.AgentExecutionPipeline)
            .GetMethod("BuildChartUnavailableDiagnostic",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        m.Should().NotBeNull("the fail-closed diagnostic helper must remain in place");
        string diagnostic = (string)m!.Invoke(null, ["horizontalBar"])!;

        AssertNoBannedPhrases(diagnostic);
        diagnostic.Should().Contain("Chart unavailable");
    }

    [Fact]
    public void BudgetCapNotice_ContainsNoBannedFallbackPhrases()
    {
        string notice = Api.Budget.BudgetedAIFunction.BuildBudgetCapNotice(5);
        AssertNoBannedPhrases(notice);
    }

    private static void AssertNoBannedPhrases(string text)
    {
        // The exact phrasings the model hallucinated onto the P0 chart card. Any
        // occurrence anywhere in a system diagnostic re-primes the model to repeat
        // that narrative.
        string[] banned =
        [
            "truncated",
            "placeholder zero",
            "should not be used",
            "chart shell",
            "unable to rank",
        ];
        foreach (string p in banned)
        {
            text.Should().NotContainEquivalentOf(p, $"the diagnostic must never contain '{p}'");
        }
    }
}

using FluentAssertions;
using RetailPulse.Api.Agents;
using Xunit;

namespace RetailPulse.Tests.Agents;

/// <summary>
/// Publix production sweep #76 Group G — the forbidden fallback / truncation
/// vocabulary must be scrubbed from EVERY final user-visible reply, not only
/// the portfolio-ranking fulfillment path. Prompts #2, #5 (prose) and #21
/// (chart-returns-no-chart) each shipped "truncated" in the reply.
///
/// This suite pins the last-mile scrub contract:
/// * sentences carrying banned tokens are removed;
/// * legitimate prose is preserved verbatim;
/// * a reply that consists ENTIRELY of banned tokens is NOT substituted with
///   the chart-oriented neutral confirmation (that neutral message is
///   reserved for <see cref="AgentExecutionPipeline.StripFallbackClaims"/>,
///   which only fires on the roster-chart path).
/// </summary>
public sealed class GlobalReplyScrubTests
{
    [Theory]
    [InlineData("The dataset was truncated so I fell back to a placeholder chart.")]
    [InlineData("Falling back to a chart shell because the data is unavailable.")]
    [InlineData("Historical demand pulls were truncated for one or more regions.")]
    public void ScrubBannedSentences_RemovesBannedSentences(string reply)
    {
        string cleaned = AgentExecutionPipeline.ScrubBannedSentences(reply);
        foreach (string phrase in new[]
                 {
                     "truncated", "placeholder zero", "should not be used",
                     "chart shell", "unable to rank", "falling back", "fallback",
                 })
        {
            cleaned.Should().NotContainEquivalentOf(phrase,
                $"the final assistant reply must never carry '{phrase}'");
        }
    }

    [Fact]
    public void ScrubBannedSentences_PreservesLegitimateProseUnchanged()
    {
        const string legit = "Depletions are up 3.2% year-over-year across the Southeast portfolio.";
        AgentExecutionPipeline.ScrubBannedSentences(legit).Should().Be(legit);
    }

    [Fact]
    public void ScrubBannedSentences_DoesNotSubstituteChartOrientedNeutralMessage()
    {
        // A non-chart reply that is entirely banned tokens must NOT be swapped
        // for the portfolio-ranking neutral message — the last-mile scrub is
        // used for prose prompts too.
        const string allBanned = "truncated. fallback.";
        string cleaned = AgentExecutionPipeline.ScrubBannedSentences(allBanned);
        cleaned.Should().NotContainEquivalentOf("portfolio ranking");
    }

    [Fact]
    public void ScrubBannedSentences_HandlesNullAndEmpty()
    {
        AgentExecutionPipeline.ScrubBannedSentences(null).Should().Be(string.Empty);
        AgentExecutionPipeline.ScrubBannedSentences("").Should().Be(string.Empty);
    }
}

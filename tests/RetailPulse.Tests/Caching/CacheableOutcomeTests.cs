using FluentAssertions;
using RetailPulse.Api.Middleware;

namespace RetailPulse.Tests.Caching;

/// <summary>
/// A cacheable QUESTION is not the same thing as a cacheable ANSWER.
/// </summary>
/// <remarks>
/// Chat responses were stored unconditionally whenever the prompt looked deterministic,
/// so a degraded reply was replayed for the full five-minute TTL. One transient model
/// wobble became a prompt that failed identically for every subsequent visitor.
///
/// Observed live on a curated prompt shipped in the welcome grid: "Show a horizontal bar
/// chart ranking all brands by depletion growth rate" returned "Chart unavailable" with a
/// single <c>cache.hit</c> span and no tool calls at all — the failure was being served
/// from cache, not recomputed. Failing once is a model problem; failing repeatably from
/// cache is ours.
/// </remarks>
public class CacheableOutcomeTests
{
    private const string GoodReply = "FreshMart depletions in the Northeast are up 3.5% year over year.";

    [Fact]
    public void CachesAGoodProseAnswer()
    {
        CacheHelpers.IsCacheableOutcome(GoodReply, chartCount: 0, isErrorResponse: false, chartWasRequested: false)
            .Should().BeTrue();
    }

    [Fact]
    public void CachesAChartAnswerThatActuallyProducedAChart()
    {
        CacheHelpers.IsCacheableOutcome(GoodReply, chartCount: 1, isErrorResponse: false, chartWasRequested: true)
            .Should().BeTrue();
    }

    [Fact]
    public void DoesNotCacheAChartRequestThatProducedNoChart()
    {
        // This is the exact shape of the live failure: the prompt asked for a chart, the
        // pipeline failed closed, and the empty result was then served to everyone else.
        CacheHelpers.IsCacheableOutcome(GoodReply, chartCount: 0, isErrorResponse: false, chartWasRequested: true)
            .Should().BeFalse();
    }

    [Fact]
    public void DoesNotCacheThePipelinesChartUnavailableDiagnostic()
    {
        const string diagnostic =
            "⚠️ Chart unavailable: a portfolio ranking must cover every configured brand "
            + "(12 total for this tenant), but the following were not returned by the underlying data tools.";

        CacheHelpers.IsCacheableOutcome(diagnostic, chartCount: 0, isErrorResponse: false, chartWasRequested: true)
            .Should().BeFalse();
    }

    [Fact]
    public void DoesNotCacheTheCouldNotGenerateApology()
    {
        CacheHelpers.IsCacheableOutcome(
            "I wasn't able to generate a response.", chartCount: 0, isErrorResponse: false, chartWasRequested: false)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("⏳ The request timed out. Please try again.")]
    [InlineData("⚠️ Rate limit reached.")]
    public void DoesNotCacheAResponseThePipelineFlaggedAsAnError(string reply)
    {
        CacheHelpers.IsCacheableOutcome(reply, chartCount: 0, isErrorResponse: true, chartWasRequested: false)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DoesNotCacheAnEmptyReply(string reply)
    {
        CacheHelpers.IsCacheableOutcome(reply, chartCount: 0, isErrorResponse: false, chartWasRequested: false)
            .Should().BeFalse();
    }

    [Fact]
    public void StillCachesProseThatMerelyMentionsCharts()
    {
        // A prose answer that happens to discuss charts is not a failed chart request.
        CacheHelpers.IsCacheableOutcome(
            "The bar chart in last quarter's deck showed a similar trend.",
            chartCount: 0, isErrorResponse: false, chartWasRequested: false)
            .Should().BeTrue();
    }

    [Fact]
    public void DoesNotDependOnCasingOfTheDiagnostic()
    {
        CacheHelpers.IsCacheableOutcome(
            "chart unavailable: no chartable values", chartCount: 0, isErrorResponse: false, chartWasRequested: true)
            .Should().BeFalse();
    }
}

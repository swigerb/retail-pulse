using FluentAssertions;
using RetailPulse.McpServer.Data;

namespace RetailPulse.Tests.Data;

/// <summary>
/// Region arguments that mean "the whole portfolio" must resolve to the National
/// aggregate rather than falling through to a per-region lookup.
/// </summary>
/// <remarks>
/// Region is matched with SQL <c>LIKE</c>, so an unrecognised portfolio-wide phrasing does
/// not fail loudly — it matches no row and returns "No data found" for every brand. Only
/// the exact strings "National", "All", "aggregate" and "portfolio" were normalised, so
/// the curated prompt "Show a horizontal bar chart ranking all brands by depletion growth
/// rate" — which drove the model to pass <c>"all regions"</c> — produced an error for all
/// twelve brands. The ranking chart could not be built and the coverage guard correctly
/// reported the whole portfolio missing: a data-shape problem presenting as a chart bug.
///
/// "across all regions" is the phrasing used throughout the shipped prompt library, so
/// these are the expected inputs rather than edge cases.
/// </remarks>
public class PortfolioRegionNormalizationTests
{
    [Theory]
    [InlineData("all regions")]
    [InlineData("All Regions")]
    [InlineData("ALL REGIONS")]
    [InlineData("across all regions")]
    [InlineData("national")]
    [InlineData("National")]
    [InlineData("all")]
    [InlineData("aggregate")]
    [InlineData("portfolio")]
    [InlineData("the portfolio")]
    [InlineData("nationwide")]
    [InlineData("overall")]
    [InlineData("total")]
    [InlineData("everywhere")]
    [InlineData("all regions.")]
    [InlineData("  all regions  ")]
    public void TreatsPortfolioWidePhrasingAsNational(string region) =>
        RetailPulseDb.IsPortfolioWideRegion(region).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // A portfolio-wide growth ranking has no natural region qualifier.
    public void TreatsAMissingRegionAsPortfolioWide(string? region) =>
        RetailPulseDb.IsPortfolioWideRegion(region).Should().BeTrue();

    [Theory]
    [InlineData("Northeast")]
    [InlineData("Southeast")]
    [InlineData("Midwest")]
    [InlineData("Southwest")]
    [InlineData("West Coast")]
    [InlineData("Pacific Northwest")]
    // Every region configured in the default pack must still scope the query.
    public void LeavesARealRegionAlone(string region) =>
        RetailPulseDb.IsPortfolioWideRegion(region).Should().BeFalse();

    [Fact]
    public void MatchesTheWholeArgumentNotASubstring()
    {
        // A future region whose name merely contains a keyword must not collapse to
        // National — the match is on the entire argument, not a contains() check.
        RetailPulseDb.IsPortfolioWideRegion("Total Wine Region").Should().BeFalse();
        RetailPulseDb.IsPortfolioWideRegion("All Saints District").Should().BeFalse();
    }
}

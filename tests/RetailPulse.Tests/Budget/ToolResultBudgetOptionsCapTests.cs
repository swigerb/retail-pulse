using FluentAssertions;
using RetailPulse.Api.Budget;
using Xunit;

namespace RetailPulse.Tests.Budget;

/// <summary>
/// Publix production sweep #76 Group F — the per-request tool-call cap must
/// be tight enough to prevent per-region sequential fan-out on prompts that
/// are NOT classified as chart-intent (e.g. #1 "Compare depletion trends
/// across all regions for this quarter", #8, #17). Those hit 8 calls in prod
/// because the non-chart cap was 8. Any answer needing more than 5 distinct
/// tool invocations should be answered by an aggregate tool
/// (GetPortfolioDepletionStats, GetHistoricalDemand with region=National),
/// not per-region fan-out.
/// </summary>
public sealed class ToolResultBudgetOptionsCapTests
{
    [Fact]
    public void MaxToolCalls_DefaultsToFive()
    {
        var options = new ToolResultBudgetOptions();
        options.MaxToolCalls.Should().Be(5,
            "the global distinct-call cap must be 5 to prevent per-region fan-out on non-chart prompts");
    }

    [Fact]
    public void MaxToolCallsForChartIntent_IsAtMostMaxToolCalls()
    {
        var options = new ToolResultBudgetOptions();
        options.MaxToolCallsForChartIntent.Should().BeLessThanOrEqualTo(options.MaxToolCalls,
            "the chart-intent cap must never exceed the global cap");
    }

    [Fact]
    public void MaxCumulativeChars_MatchesDocumentedBudget()
    {
        var options = new ToolResultBudgetOptions();
        options.MaxCumulativeChars.Should().Be(25_000,
            "the documented tool-context budget is 25K characters");
    }
}

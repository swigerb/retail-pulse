using FluentAssertions;
using RetailPulse.Contracts.Caching;

namespace RetailPulse.Tests.Middleware;

/// <summary>
/// Tests for QueryClassifier — deterministic query classification for caching decisions.
/// Covers: factual queries (cacheable), recommendation/forecast queries (non-cacheable),
/// time-sensitive queries, agent-level exclusions, General agent defaults.
/// </summary>
public class DeterministicClassifierTests
{
    #region Deterministic (Cacheable) Queries

    [Theory]
    [InlineData("What is brand X?")]
    [InlineData("What are the top brands in Southeast?")]
    [InlineData("Define market share")]
    [InlineData("How does the supply chain work?")]
    [InlineData("Explain the pricing model")]
    public void IsDeterministic_FactualQuery_GeneralAgent_ReturnsTrue(string query)
    {
        QueryClassifier.IsDeterministic(query, "general")
            .Should().BeTrue($"'{query}' is a factual query on general agent");
    }

    [Theory]
    [InlineData("Show me last quarter's data")]
    [InlineData("Historical sales from last year")]
    [InlineData("What were FY2025 results?")]
    [InlineData("Q3 2025 performance")]
    [InlineData("Last month revenue breakdown")]
    public void IsDeterministic_HistoricalQuery_ReturnsTrue(string query)
    {
        QueryClassifier.IsDeterministic(query, "general")
            .Should().BeTrue($"'{query}' references historical/past data");
    }

    #endregion

    #region Non-Deterministic (Never Cache)

    [Theory]
    [InlineData("What should I do about declining sales?")]
    [InlineData("Should I increase the promotion budget?")]
    [InlineData("Recommend a promotion for next quarter")]
    [InlineData("Suggest the best supply route")]
    public void IsDeterministic_RecommendationQuery_ReturnsFalse(string query)
    {
        QueryClassifier.IsDeterministic(query, "general")
            .Should().BeFalse($"'{query}' asks for a recommendation");
    }

    [Theory]
    [InlineData("Forecast demand for next month")]
    [InlineData("Predict sales for Q4")]
    public void IsDeterministic_ForecastQuery_ReturnsFalse(string query)
    {
        QueryClassifier.IsDeterministic(query, "general")
            .Should().BeFalse($"'{query}' asks for a forecast");
    }

    [Theory]
    [InlineData("What are sales today?")]
    [InlineData("Show me this week's numbers")]
    [InlineData("Current inventory levels")]
    [InlineData("What's happening right now?")]
    [InlineData("Live sales data")]
    [InlineData("This month's performance")]
    public void IsDeterministic_TimeSensitiveQuery_ReturnsFalse(string query)
    {
        QueryClassifier.IsDeterministic(query, "general")
            .Should().BeFalse($"'{query}' is time-sensitive");
    }

    #endregion

    #region Agent-Level Exclusions

    [Theory]
    [InlineData("What is brand X?")]
    [InlineData("Show me last quarter's data")]
    [InlineData("Define market share")]
    public void IsDeterministic_DemandForecastAgent_AlwaysReturnsFalse(string query)
    {
        QueryClassifier.IsDeterministic(query, "demand-forecasting")
            .Should().BeFalse("demand-forecasting agent output is inherently non-deterministic");
    }

    [Fact]
    public void IsDeterministic_DemandForecastAgent_CaseInsensitive()
    {
        QueryClassifier.IsDeterministic("What is brand X?", "DEMAND-FORECASTING")
            .Should().BeFalse();
        QueryClassifier.IsDeterministic("What is brand X?", "Demand-Forecasting")
            .Should().BeFalse();
    }

    #endregion

    #region General Agent Default Behavior

    [Fact]
    public void IsDeterministic_GeneralAgent_AmbiguousQuery_DefaultsToTrue()
    {
        // Query doesn't match any always-cache or never-cache pattern
        // General agent defaults to deterministic
        QueryClassifier.IsDeterministic("Tell me about the Southeast region", "general")
            .Should().BeTrue("General agent defaults to deterministic for ambiguous queries");
    }

    [Fact]
    public void IsDeterministic_NonGeneralAgent_AmbiguousQuery_DefaultsToFalse()
    {
        // Same ambiguous query but for a specialist agent → not cached
        QueryClassifier.IsDeterministic("Tell me about the Southeast region", "promo-planning")
            .Should().BeFalse("specialist agents default to non-deterministic for ambiguous queries");
    }

    #endregion

    #region Edge Cases

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsDeterministic_EmptyOrNullQuery_ReturnsFalse(string? query)
    {
        QueryClassifier.IsDeterministic(query!, "general")
            .Should().BeFalse("empty/null queries are never cacheable");
    }

    [Fact]
    public void IsDeterministic_MixedSignals_NeverCacheTakesPrecedence()
    {
        // "What is" (always cache) + "today" (never cache) → never cache wins
        QueryClassifier.IsDeterministic("What is the forecast for today?", "general")
            .Should().BeFalse("never-cache pattern takes precedence");
    }

    #endregion
}

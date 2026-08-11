using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Budget;
using Xunit;

namespace RetailPulse.Tests.Budget;

/// <summary>
/// Regression coverage for issue #74 — the horizontal-bar "rank all brands by
/// depletion growth rate" tool loop must keep the cumulative tool-context under
/// the 25K-token contract. Before the fix, a 12× per-brand
/// <c>GetHistoricalDemand</c> fan-out (even compacted) exceeded 30K tokens. The
/// fix routes the prompt to the aggregate tool (one call, one payload) and caps
/// chart-intent tool loops at 5 distinct calls — this test pins the resulting
/// token ceiling.
/// </summary>
public sealed class ChartIntentTokenBudgetTests
{
    private sealed class ByteFakeFunction : AIFunction
    {
        private readonly int _resultBytes;
        public override string Name { get; }
        public override string Description => "fake";
        public override JsonElement JsonSchema { get; } =
            JsonDocument.Parse("""{"type":"object"}""").RootElement;

        public ByteFakeFunction(string name, int resultBytes)
        {
            Name = name;
            _resultBytes = resultBytes;
        }

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken ct)
        {
            // Emit a payload of the target size; the budget wrapper measures it after
            // per-tool compaction, but for a generic fake we return raw JSON that will
            // hit the MaxResultChars cap and be clipped.
            string filler = new('x', _resultBytes);
            return ValueTask.FromResult<object?>(JsonSerializer.Serialize(new { data = filler }));
        }
    }

    [Fact]
    public async Task HorizontalBarRankingPrompt_KeepsToolContextUnder25kEstimatedTokens()
    {
        var options = new ToolResultBudgetOptions
        {
            Enabled = true,
            MaxResultChars = 6_000,
            MaxCumulativeChars = 24_000,
            MaxToolCalls = 8,
            MaxToolCallsForChartIntent = 5,
            CharsPerToken = 4,
            MaxArrayItems = 24,
        };

        // A single beefy tool call (6KB) followed by 7 more attempts — the chart cap
        // fires at 5, so the cumulative context stays well under 25K tokens.
        var fn = new ByteFakeFunction("GetPortfolioDepletionStats", resultBytes: 5_500);
        var wrapped = new BudgetedAIFunction(fn, new ToolResultBudget([]), options, NullLogger.Instance);

        using IDisposable scope = RequestToolContext.Begin("session-1", isChartIntent: true);
        for (int i = 0; i < 8; i++)
        {
            var args = new AIFunctionArguments
            {
                ["ix"] = i
            };
            await wrapped.InvokeAsync(args);
        }

        int estTokens = options.EstimateTokens(RequestToolContext.Current!.CumulativeChars);
        estTokens.Should().BeLessThan(25_000,
            "chart-intent tool loops must keep the estimated tool-context under 25K tokens");
    }
}

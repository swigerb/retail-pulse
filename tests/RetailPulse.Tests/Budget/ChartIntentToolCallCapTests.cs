using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Budget;
using Xunit;

namespace RetailPulse.Tests.Budget;

/// <summary>
/// Regression coverage for issue #74 — chart-intent tool-call cap and diagnostic
/// wording. The P0 failure was: a horizontal-bar ranking prompt was routed to a
/// specialist without the aggregate tool; it fanned out per-brand calls until the
/// generic 8-call budget fired; the diagnostic contained the word "truncated";
/// the model then parroted a "truncated / placeholder zeros" refusal back to the
/// user. This suite pins:
///  * chart-intent scopes cap distinct calls at <see cref="ToolResultBudgetOptions.MaxToolCallsForChartIntent"/> (=5);
///  * the cap diagnostic never contains "truncated" or "placeholder";
///  * the cap diagnostic instructs the model to synthesise from what it has.
/// </summary>
public sealed class ChartIntentToolCallCapTests
{
    private sealed class CountingFunction : AIFunction
    {
        private readonly Func<AIFunctionArguments, string> _body;
        public int Invocations { get; private set; }
        public override string Name { get; }
        public override string Description => "fake";
        public override JsonElement JsonSchema { get; } =
            JsonDocument.Parse("""{"type":"object","properties":{"brand":{"type":"string"}}}""").RootElement;

        public CountingFunction(string name, Func<AIFunctionArguments, string> body)
        {
            Name = name;
            _body = body;
        }

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken ct)
        {
            Invocations++;
            return ValueTask.FromResult<object?>(_body(arguments));
        }
    }

    private static BudgetedAIFunction Wrap(AIFunction inner, ToolResultBudgetOptions options) =>
        new(inner, new ToolResultBudget([]), options, NullLogger.Instance);

    private static AIFunctionArguments Args(string brand)
    {
        var a = new AIFunctionArguments
        {
            ["brand"] = brand
        };
        return a;
    }

    private static ToolResultBudgetOptions DefaultOptions() => new()
    {
        Enabled = true,
        MaxResultChars = 6000,
        MaxCumulativeChars = 200_000,
        MaxToolCalls = 8,
        MaxToolCallsForChartIntent = 5,
        CharsPerToken = 4,
        MaxArrayItems = 24,
    };

    [Fact]
    public async Task ChartIntent_HardCapsDistinctToolCallsAtFive()
    {
        var inner = new CountingFunction("GetHistoricalDemand",
            a => JsonSerializer.Serialize(new { brand = a["brand"] }));
        BudgetedAIFunction fn = Wrap(inner, DefaultOptions());

        using IDisposable scope = RequestToolContext.Begin("session-1", isChartIntent: true);

        object?[] results = new object?[8];
        for (int i = 0; i < 8; i++)
        {
            results[i] = await fn.InvokeAsync(Args($"Brand{i}"));
        }

        inner.Invocations.Should().Be(5,
            "chart-intent scopes must cap distinct tool invocations at 5, not 8");

        // Calls 6, 7, 8 must all return the budget_notice diagnostic.
        for (int i = 5; i < 8; i++)
        {
            using var doc = JsonDocument.Parse((string)results[i]!);
            doc.RootElement.TryGetProperty("budget_notice", out _)
                .Should().BeTrue($"invocation #{i + 1} is beyond the chart-intent cap");
        }
    }

    [Fact]
    public async Task ChartIntent_UsesLowerOfMaxToolCallsAndChartCap()
    {
        // Even if the general cap is looser (12), the chart-intent cap (5) wins.
        ToolResultBudgetOptions options = DefaultOptions();
        options.MaxToolCalls = 12;
        options.MaxToolCallsForChartIntent = 5;

        var inner = new CountingFunction("GetHistoricalDemand",
            a => JsonSerializer.Serialize(new { brand = a["brand"] }));
        BudgetedAIFunction fn = Wrap(inner, options);

        using IDisposable scope = RequestToolContext.Begin("session-1", isChartIntent: true);
        for (int i = 0; i < 8; i++)
        {
            await fn.InvokeAsync(Args($"Brand{i}"));
        }

        inner.Invocations.Should().Be(5);
    }

    [Fact]
    public async Task NonChartIntent_UsesGeneralCap()
    {
        // No chart intent → the general cap of 8 applies, not the chart cap of 5.
        var inner = new CountingFunction("GetHistoricalDemand",
            a => JsonSerializer.Serialize(new { brand = a["brand"] }));
        BudgetedAIFunction fn = Wrap(inner, DefaultOptions());

        using IDisposable scope = RequestToolContext.Begin("session-1", isChartIntent: false);
        for (int i = 0; i < 10; i++)
        {
            await fn.InvokeAsync(Args($"Brand{i}"));
        }

        inner.Invocations.Should().Be(8,
            "a non-chart scope must fall back to MaxToolCalls (8), not the chart cap");
    }

    [Fact]
    public void BudgetNotice_DoesNotContainWord_Truncated()
    {
        string message = BudgetedAIFunction.BuildBudgetCapNotice(5);

        message.Should().NotContain("truncated", "the word 'truncated' primed the model to hallucinate a refusal narrative");
        message.Should().NotContain("placeholder", "the word 'placeholder' primed the model to describe zeros as fake");
        message.Should().NotContain("unavailable");
        message.Should().Contain("COMPLETE",
            "the cap notice must positively instruct the model that the results already gathered are complete");
        message.Should().Contain("CreateChart",
            "the cap notice must instruct the model to synthesise and call CreateChart");
    }

    [Fact]
    public async Task CapDiagnostic_JsonPayload_ContainsNoBannedPhrases()
    {
        var inner = new CountingFunction("GetHistoricalDemand",
            a => JsonSerializer.Serialize(new { brand = a["brand"] }));
        BudgetedAIFunction fn = Wrap(inner, DefaultOptions());

        using IDisposable scope = RequestToolContext.Begin("session-1", isChartIntent: true);
        object? last = null;
        for (int i = 0; i < 7; i++)
        {
            last = await fn.InvokeAsync(Args($"Brand{i}"));
        }

        string json = (string)last!;
        json.Should().NotContain("truncated");
        json.Should().NotContain("placeholder");
    }

    /// <summary>
    /// Issue #76 Group F: the per-request explicit-chart intent cap default is
    /// <see cref="ToolResultBudgetOptions.MaxToolCallsForChartIntent"/> = 5 and
    /// <see cref="BudgetedAIFunction"/> enforces it using ONLY the default options
    /// (no explicit override). Regressing either the constant or the read site
    /// silently reopens the fan-out failure class from the P0 sweep.
    /// </summary>
    [Fact]
    public async Task ChartIntent_UsesDefaultCapOfFive_WithoutExplicitOverride()
    {
        // Take the shipped defaults verbatim — this test would fail if a future
        // config change lowered the default under 5 or raised it above 5 without
        // the caller opting in explicitly.
        ToolResultBudgetOptions shipped = new();
        shipped.MaxToolCallsForChartIntent.Should().Be(5,
            "ADR-006 tool-context budget: explicit chart intents are hard-capped at 5 distinct tool calls");

        var inner = new CountingFunction("GetHistoricalDemand",
            a => JsonSerializer.Serialize(new { brand = a["brand"] }));
        BudgetedAIFunction fn = Wrap(inner, shipped);

        using IDisposable scope = RequestToolContext.Begin("session-defaults", isChartIntent: true);
        for (int i = 0; i < 9; i++)
        {
            await fn.InvokeAsync(Args($"Brand{i}"));
        }

        inner.Invocations.Should().Be(5,
            "BudgetedAIFunction must read the shipped default MaxToolCallsForChartIntent (5) " +
            "when the caller does not override it — issue #76 Group F");
    }
}

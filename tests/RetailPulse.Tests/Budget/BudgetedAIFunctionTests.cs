using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Budget;
using Xunit;

namespace RetailPulse.Tests.Budget;

/// <summary>
/// Tests the request-scoped behavior of <see cref="BudgetedAIFunction"/> +
/// <see cref="RequestToolContext"/>: dedup of identical calls, distinct arguments staying
/// distinct, the distinct-call cap, the cumulative per-request budget, exempt passthrough,
/// and principal-scoped isolation.
/// </summary>
public sealed class BudgetedAIFunctionTests
{
    private static ToolResultBudget CreateBudget() =>
        new([new HistoricalDemandCompactor(), new PortfolioDepletionCompactor()]);

    private static ToolResultBudgetOptions Options(int maxCumulative = 24_000, int maxCalls = 8) => new()
    {
        Enabled = true,
        MaxResultChars = 6000,
        MaxCumulativeChars = maxCumulative,
        MaxToolCalls = maxCalls,
        CharsPerToken = 4,
        MaxArrayItems = 24
    };

    /// <summary>A counting fake tool whose result is controllable per-invocation.</summary>
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
        new(inner, CreateBudget(), options, NullLogger.Instance);

    private static AIFunctionArguments Args(params (string key, object? value)[] pairs)
    {
        var a = new AIFunctionArguments();
        foreach ((string key, object? value) in pairs)
            a[key] = value;
        return a;
    }

    [Fact]
    public async Task IdenticalCall_IsDeduplicated_InnerInvokedOnce()
    {
        var inner = new CountingFunction("GetDepletionStats",
            _ => JsonSerializer.Serialize(new { brand = "Apex Grill", v = 1 }));
        BudgetedAIFunction fn = Wrap(inner, Options());

        using IDisposable scope = RequestToolContext.Begin("session-1");
        object? first = await fn.InvokeAsync(Args(("brand", "Apex Grill")));
        object? second = await fn.InvokeAsync(Args(("brand", "Apex Grill")));

        inner.Invocations.Should().Be(1, "the identical second call is served from the dedup cache");
        first.Should().Be(second);

        RequestToolContext.Current!.DistinctCalls.Should().Be(1);
        RequestToolContext.Current!.Metrics.Should().Contain(m => m.Deduplicated);
    }

    [Fact]
    public async Task DistinctArguments_StayDistinct()
    {
        var inner = new CountingFunction("GetDepletionStats",
            a => JsonSerializer.Serialize(new { brand = a["brand"], v = 1 }));
        BudgetedAIFunction fn = Wrap(inner, Options());

        using IDisposable scope = RequestToolContext.Begin("session-1");
        await fn.InvokeAsync(Args(("brand", "Apex Grill")));
        await fn.InvokeAsync(Args(("brand", "Coastline Tacos")));

        inner.Invocations.Should().Be(2, "different brands are genuinely different calls");
        RequestToolContext.Current!.DistinctCalls.Should().Be(2);
    }

    [Fact]
    public async Task ArgumentOrder_DoesNotDefeatDedup()
    {
        var inner = new CountingFunction("GetDepletionStats",
            _ => JsonSerializer.Serialize(new { ok = true }));
        BudgetedAIFunction fn = Wrap(inner, Options());

        using IDisposable scope = RequestToolContext.Begin("session-1");
        await fn.InvokeAsync(Args(("brand", "Apex Grill"), ("region", "National")));
        await fn.InvokeAsync(Args(("region", "National"), ("brand", "Apex Grill")));

        inner.Invocations.Should().Be(1, "arguments are normalized to a stable, order-independent key");
    }

    [Fact]
    public async Task DistinctCallCap_ReturnsDiagnostic_WithoutInvoking()
    {
        var inner = new CountingFunction("GetDepletionStats",
            a => JsonSerializer.Serialize(new { brand = a["brand"] }));
        BudgetedAIFunction fn = Wrap(inner, Options(maxCalls: 2));

        using IDisposable scope = RequestToolContext.Begin("session-1");
        await fn.InvokeAsync(Args(("brand", "A")));
        await fn.InvokeAsync(Args(("brand", "B")));
        object? third = await fn.InvokeAsync(Args(("brand", "C")));

        inner.Invocations.Should().Be(2, "the 3rd distinct call is refused by the cap");
        using var doc = JsonDocument.Parse((string)third!);
        doc.RootElement.TryGetProperty("budget_notice", out _).Should().BeTrue();
    }

    [Fact]
    public async Task CumulativeBudget_WithholdsFurtherResults()
    {
        // Each call returns ~4000 chars; a 6000-char cumulative cap admits one, withholds the next.
        var inner = new CountingFunction("BigTool",
            a => JsonSerializer.Serialize(new { brand = a["brand"], blob = new string('x', 4000) }));
        BudgetedAIFunction fn = Wrap(inner, Options(maxCumulative: 6000));

        using IDisposable scope = RequestToolContext.Begin("session-1");
        object? first = await fn.InvokeAsync(Args(("brand", "A")));
        object? second = await fn.InvokeAsync(Args(("brand", "B")));

        ((string)first!).Should().Contain("blob", "the first result fits under the cumulative cap");
        using var doc = JsonDocument.Parse((string)second!);
        doc.RootElement.TryGetProperty("budget_notice", out _).Should().BeTrue(
            "the second result exceeds the cumulative budget and is withheld");
    }

    [Fact]
    public async Task ExemptTool_PassesThrough_AndDoesNotCountCumulatively()
    {
        string bigChart = JsonSerializer.Serialize(new { type = "grouped-bar", blob = new string('c', 10_000) });
        var inner = new CountingFunction("CreateChart", _ => bigChart);
        BudgetedAIFunction fn = Wrap(inner, Options(maxCumulative: 6000));

        using IDisposable scope = RequestToolContext.Begin("session-1");
        object? result = await fn.InvokeAsync(Args());

        ((string)result!).Should().Be(bigChart, "the canonical ChartSpec is never compacted");
        RequestToolContext.Current!.CumulativeChars.Should().Be(0,
            "exempt tools do not consume the cumulative budget");
    }

    [Fact]
    public async Task NoActiveScope_PassesThrough_Unbudgeted()
    {
        string big = JsonSerializer.Serialize(new { blob = new string('x', 50_000) });
        var inner = new CountingFunction("BigTool", _ => big);
        BudgetedAIFunction fn = Wrap(inner, Options());

        // No RequestToolContext.Begin — e.g. the simplified/test pipeline path.
        object? result = await fn.InvokeAsync(Args());

        ((string)result!).Should().Be(big);
        inner.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task Dedup_IsScopedToTheRequest_NotAcrossScopes()
    {
        var inner = new CountingFunction("GetDepletionStats",
            _ => JsonSerializer.Serialize(new { ok = true }));
        BudgetedAIFunction fn = Wrap(inner, Options());

        using (RequestToolContext.Begin("session-1"))
        {
            await fn.InvokeAsync(Args(("brand", "A")));
        }
        using (RequestToolContext.Begin("session-2"))
        {
            await fn.InvokeAsync(Args(("brand", "A")));
        }

        inner.Invocations.Should().Be(2, "a new request scope starts with an empty dedup cache");
    }
}

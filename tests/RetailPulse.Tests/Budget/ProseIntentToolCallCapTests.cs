using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Budget;
using Xunit;

namespace RetailPulse.Tests.Budget;

/// <summary>
/// Publix production sweep #76 — the per-request distinct-call cap MUST apply to
/// prose / non-chart-intent requests as well. Prompt #17 ("Compare Foundry Home
/// vs Urban Living performance in the West Coast") fanned out to 7 distinct tool
/// calls because <c>appsettings.json</c> was overriding the code default from 5
/// back up to 8. This suite pins:
/// <list type="bullet">
///   <item>the default runtime configuration bound from <c>appsettings.json</c>
///     enforces a 5-call cap on both chart and non-chart intents;</item>
///   <item>a two-brand comparison scope is hard-stopped at 5 distinct invocations,
///     regardless of chart-intent classification, so the answer is synthesised
///     from the aggregate results already gathered rather than parroting a
///     truncation refusal.</item>
/// </list>
/// </summary>
public sealed class ProseIntentToolCallCapTests
{
    private sealed class CountingFunction : AIFunction
    {
        private readonly Func<AIFunctionArguments, string> _body;
        public int Invocations { get; private set; }
        public override string Name { get; }
        public override string Description => "counting";
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

    private static AIFunctionArguments Args(string brand) =>
        new() { ["brand"] = brand };

    /// <summary>
    /// Options bound the way the API actually binds them at runtime — from the
    /// canonical <c>appsettings.json</c> section. If <c>appsettings.json</c>
    /// silently overrode the code default back up to 8 this test would fail.
    /// </summary>
    private static ToolResultBudgetOptions BindFromAppSettings()
    {
        string appsettingsPath = ResolveAppSettingsPath();
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddJsonFile(appsettingsPath, optional: false)
            .Build();
        var options = new ToolResultBudgetOptions();
        config.GetSection(ToolResultBudgetOptions.SectionName).Bind(options);
        return options;
    }

    private static string ResolveAppSettingsPath()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "RetailPulse.Api", "appsettings.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("appsettings.json for RetailPulse.Api not found from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void AppSettings_MaxToolCalls_IsFive_ForBothProseAndChartIntent()
    {
        ToolResultBudgetOptions options = BindFromAppSettings();

        options.MaxToolCalls.Should().Be(5,
            "the runtime config must not override the 5-call default back up to 8 — "
            + "prose/comparison prompts (#17) fanned out to 7 calls when it did");
        options.MaxToolCallsForChartIntent.Should().BeLessThanOrEqualTo(5,
            "the chart-intent cap must never exceed the general 5-call cap");
    }

    [Fact]
    public async Task ProseComparisonScope_HardCapsDistinctToolCallsAtFive()
    {
        ToolResultBudgetOptions options = BindFromAppSettings();
        var inner = new CountingFunction("GetDepletionStats",
            a => JsonSerializer.Serialize(new { brand = a["brand"] }));
        BudgetedAIFunction fn = Wrap(inner, options);

        // Simulate the exact Publix sweep #17 shape: a two-brand West-Coast
        // comparison that is NOT classified as a chart intent. The specialist
        // fanned out 7 distinct calls in production; the cap must stop it at 5.
        using IDisposable scope = RequestToolContext.Begin("session-17", isChartIntent: false);

        object?[] results = new object?[8];
        for (int i = 0; i < 8; i++)
        {
            results[i] = await fn.InvokeAsync(Args($"Brand{i}"));
        }

        inner.Invocations.Should().Be(5,
            "prose comparison scopes must cap distinct tool invocations at 5, "
            + "not 7 or 8 — otherwise the 25K tool-context budget is blown and the "
            + "model parrots a truncation narrative back to the user");

        // Every call beyond the cap must return the budget_notice diagnostic
        // (never a payload) so the model synthesises from what it has.
        for (int i = 5; i < 8; i++)
        {
            using var doc = JsonDocument.Parse((string)results[i]!);
            doc.RootElement.TryGetProperty("budget_notice", out _)
                .Should().BeTrue($"invocation #{i + 1} is beyond the prose cap");
        }
    }

    [Fact]
    public async Task ProseScope_CapDiagnostic_ContainsNoBannedFallbackVocabulary()
    {
        ToolResultBudgetOptions options = BindFromAppSettings();
        var inner = new CountingFunction("GetDepletionStats",
            a => JsonSerializer.Serialize(new { brand = a["brand"] }));
        BudgetedAIFunction fn = Wrap(inner, options);

        using IDisposable scope = RequestToolContext.Begin("session-17", isChartIntent: false);
        object? last = null;
        for (int i = 0; i < 7; i++)
        {
            last = await fn.InvokeAsync(Args($"Brand{i}"));
        }

        string json = (string)last!;
        json.Should().NotContain("truncated");
        json.Should().NotContain("placeholder");
        json.Should().NotContain("unavailable");
        json.Should().Contain("COMPLETE",
            "prose cap diagnostics must positively instruct the model that what it has is complete");
    }
}

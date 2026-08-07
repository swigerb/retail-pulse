using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RetailPulse.Api.Budget;

/// <summary>
/// Outermost tool wrapper that enforces the tool-context budget before a result enters
/// model context:
/// <list type="number">
///   <item>Request-scoped dedup — an identical (name+args+principal) call within the same
///   request returns the earlier compact result without re-executing.</item>
///   <item>Per-request distinct-call cap — beyond the cap, a compact diagnostic is returned
///   instead of invoking, so runaway tool loops cannot explode context.</item>
///   <item>Per-result compaction via <see cref="ToolResultBudget"/> (tool-specific
///   summarizers, then generic truncation, then a valid hard clip).</item>
///   <item>Cumulative per-request budget — once exceeded, further results are replaced by a
///   compact diagnostic.</item>
/// </list>
/// Exempt tools (e.g. <c>CreateChart</c>) pass through untouched and do not count toward
/// the cumulative budget, so the canonical ChartSpec the frontend needs is never compacted.
/// Telemetry records sizes/flags only — never payload content.
/// </summary>
internal sealed class BudgetedAIFunction : AIFunction
{
    private readonly AIFunction _inner;
    private readonly ToolResultBudget _budget;
    private readonly ToolResultBudgetOptions _options;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions _argSerialization = new()
    {
        // Deterministic-ish serialization for stable dedup keys.
        WriteIndented = false
    };

    public BudgetedAIFunction(AIFunction inner, ToolResultBudget budget, ToolResultBudgetOptions options, ILogger logger)
    {
        _inner = inner;
        _budget = budget;
        _options = options;
        _logger = logger;
    }

    public override string Name => _inner.Name;
    public override string Description => _inner.Description;
    public override JsonElement JsonSchema => _inner.JsonSchema;
    public override JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;
    public override IReadOnlyDictionary<string, object?> AdditionalProperties => _inner.AdditionalProperties;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        string toolName = _inner.Name;
        RequestToolContext? ctx = RequestToolContext.Current;

        // No active scope (e.g. legacy/test path) or boundary disabled → pass through.
        if (ctx is null || !_options.Enabled)
        {
            return await _inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
        }

        // Exempt tools carry a canonical payload — invoke and return unchanged, don't dedup
        // or count toward the cumulative budget.
        if (_options.IsExempt(toolName))
        {
            object? exemptResult = await _inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            string exemptJson = ToJson(exemptResult);
            EmitTelemetry(new ToolResultMetrics
            {
                ToolName = toolName,
                OriginalChars = exemptJson.Length,
                ReturnedChars = exemptJson.Length,
                EstimatedTokens = _options.EstimateTokens(exemptJson.Length),
                Exempt = true
            });
            return exemptResult;
        }

        string normalizedArgs = NormalizeArguments(arguments);
        string key = ctx.BuildKey(toolName, normalizedArgs);

        // 1) Dedup: identical call already served in this request.
        if (ctx.TryGetDeduped(key, out string cached))
        {
            var dedupMetrics = new ToolResultMetrics
            {
                ToolName = toolName,
                OriginalChars = cached.Length,
                ReturnedChars = cached.Length,
                EstimatedTokens = _options.EstimateTokens(cached.Length),
                Deduplicated = true
            };
            ctx.RecordDedup(dedupMetrics);
            EmitTelemetry(dedupMetrics);
            return cached;
        }

        // 2) Distinct-call cap: refuse to invoke beyond the cap.
        if (ctx.DistinctCalls >= _options.MaxToolCalls)
        {
            string capJson = Diagnostic(
                $"Tool-call budget reached ({_options.MaxToolCalls} distinct calls). "
                + "Synthesize an answer from the results already gathered instead of calling more tools.");
            var capMetrics = new ToolResultMetrics
            {
                ToolName = toolName,
                OriginalChars = 0,
                ReturnedChars = capJson.Length,
                EstimatedTokens = _options.EstimateTokens(capJson.Length),
                BudgetExceeded = true
            };
            ctx.RecordDedup(capMetrics);
            EmitTelemetry(capMetrics);
            return capJson;
        }

        // 3) Invoke and compact.
        long start = Environment.TickCount64;
        object? result = await _inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
        long durationMs = Environment.TickCount64 - start;

        string rawJson = ToJson(result);
        BudgetedResult budgeted = _budget.Apply(toolName, rawJson, _options, durationMs);
        string finalJson = budgeted.Json;
        ToolResultMetrics metrics = budgeted.Metrics;

        // 4) Cumulative per-request budget.
        if (ctx.CumulativeChars + finalJson.Length > _options.MaxCumulativeChars)
        {
            finalJson = Diagnostic(
                $"Cumulative tool-context budget ({_options.MaxCumulativeChars} chars) reached. "
                + "This result was withheld to protect the context window. Synthesize an answer from the "
                + "results already gathered, or re-call with a narrower filter.");
            metrics = metrics with
            {
                ReturnedChars = finalJson.Length,
                EstimatedTokens = _options.EstimateTokens(finalJson.Length),
                BudgetExceeded = true,
                Truncated = true
            };
        }

        ctx.Record(key, finalJson, metrics);
        EmitTelemetry(metrics);
        return finalJson;
    }

    private static string ToJson(object? result)
    {
        if (result is null) return "null";
        if (result is string s) return s;
        try
        {
            return JsonSerializer.Serialize(result, _argSerialization);
        }
        catch (NotSupportedException)
        {
            return result.ToString() ?? "";
        }
    }

    /// <summary>Deterministic normalization of tool arguments for stable dedup keys.</summary>
    private static string NormalizeArguments(AIFunctionArguments arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return "{}";

        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> kv in arguments)
        {
            sorted[kv.Key] = kv.Value switch
            {
                null => "null",
                string str => str.Trim(),
                JsonElement je => je.ToString(),
                _ => kv.Value.ToString() ?? ""
            };
        }
        return JsonSerializer.Serialize(sorted, _argSerialization);
    }

    private static string Diagnostic(string message) =>
        JsonSerializer.Serialize(new { budget_notice = message });

    private void EmitTelemetry(ToolResultMetrics m)
    {
        // Sizes/flags only — never payload content or PII.
        _logger.LogInformation(
            "ToolBudget tool={Tool} originalChars={OriginalChars} returnedChars={ReturnedChars} "
            + "estTokens={EstTokens} origItems={OrigItems} retItems={RetItems} compacted={Compacted} "
            + "truncated={Truncated} dedup={Dedup} exempt={Exempt} budgetExceeded={BudgetExceeded} durationMs={DurationMs}",
            m.ToolName, m.OriginalChars, m.ReturnedChars, m.EstimatedTokens,
            m.OriginalItems, m.ReturnedItems, m.Compacted, m.Truncated,
            m.Deduplicated, m.Exempt, m.BudgetExceeded, m.DurationMs);
    }
}

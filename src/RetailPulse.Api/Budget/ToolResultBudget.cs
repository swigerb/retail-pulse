using System.Text.Json.Nodes;

namespace RetailPulse.Api.Budget;

/// <summary>
/// Result of applying the budget boundary to a single tool result.
/// </summary>
public readonly record struct BudgetedResult(string Json, ToolResultMetrics Metrics);

/// <summary>
/// Centralized, typed compaction boundary applied to a single tool result before it
/// enters model context. Tries tool-specific summarizers first, then generic array
/// truncation, then a guaranteed-valid hard character clip — so an oversized or
/// pathological payload can never explode the context, and compaction is always
/// explicit (never silent data loss, never malformed JSON).
/// </summary>
public sealed class ToolResultBudget
{
    private readonly IReadOnlyList<IToolResultCompactor> _toolSpecific;
    private readonly GenericArrayCompactor _generic = new();

    public ToolResultBudget(IEnumerable<IToolResultCompactor> compactors)
    {
        // Only genuine tool-specific compactors participate in the ordered pass; the
        // generic array compactor is applied explicitly as the fallback.
        _toolSpecific = [.. compactors.Where(c => c is not GenericArrayCompactor)];
    }

    /// <summary>
    /// Compact a single raw tool result to fit the per-result budget. Pure and
    /// side-effect free — cumulative/dedup/iteration accounting is handled separately
    /// by the request-scoped wrapper.
    /// </summary>
    public BudgetedResult Apply(string toolName, string rawJson, ToolResultBudgetOptions options, long durationMs = 0)
    {
        rawJson ??= string.Empty;
        int originalChars = rawJson.Length;
        int maxChars = options.ResolveMaxResultChars(toolName);

        // Exempt tools (e.g. CreateChart) carry a canonical payload — never touch them.
        if (options.IsExempt(toolName))
        {
            return new BudgetedResult(rawJson, Metrics(toolName, originalChars, originalChars, options,
                exempt: true, durationMs: durationMs));
        }

        if (!options.Enabled || originalChars <= maxChars)
        {
            return new BudgetedResult(rawJson, Metrics(toolName, originalChars, originalChars, options,
                durationMs: durationMs));
        }

        bool compacted = false;
        bool truncated = false;
        int? originalItems = null;
        int? returnedItems = null;
        string current = rawJson;

        // 1) Tool-specific summarizers.
        foreach (IToolResultCompactor compactor in _toolSpecific)
        {
            if (!compactor.CanCompact(toolName))
                continue;

            ToolCompactionOutcome outcome = compactor.Compact(toolName, current, options);
            if (outcome.Changed)
            {
                current = outcome.Json;
                compacted = true;
                truncated |= outcome.Truncated;
                originalItems ??= outcome.OriginalItems;
                returnedItems = outcome.ReturnedItems;
                break;
            }
        }

        // 2) Generic array truncation if still over budget.
        if (current.Length > maxChars)
        {
            ToolCompactionOutcome generic = _generic.Compact(toolName, current, options);
            if (generic.Changed)
            {
                current = generic.Json;
                compacted = true;
                truncated = true;
                originalItems ??= generic.OriginalItems;
                returnedItems = generic.ReturnedItems;
            }
        }

        // 3) Guaranteed-valid hard clip if a pathological payload is still over budget.
        if (current.Length > maxChars)
        {
            current = HardClip(current, maxChars);
            compacted = true;
            truncated = true;
        }

        return new BudgetedResult(current, new ToolResultMetrics
        {
            ToolName = toolName,
            OriginalChars = originalChars,
            ReturnedChars = current.Length,
            OriginalItems = originalItems,
            ReturnedItems = returnedItems,
            EstimatedTokens = options.EstimateTokens(current.Length),
            Compacted = compacted,
            Truncated = truncated,
            DurationMs = durationMs
        });
    }

    /// <summary>
    /// Produces a valid-JSON diagnostic embedding a safe prefix of the original payload.
    /// Used only when structural compaction still leaves the payload over budget.
    /// </summary>
    private static string HardClip(string payload, int maxChars)
    {
        // Reserve headroom for the surrounding metadata envelope.
        int previewLen = Math.Max(0, Math.Min(payload.Length, maxChars - 512));
        string preview = payload[..previewLen];

        var envelope = new JsonObject
        {
            ["_budget"] = new JsonObject
            {
                ["over_budget"] = true,
                ["truncated"] = true,
                ["original_chars"] = payload.Length,
                ["returned_chars"] = previewLen,
                ["note"] = "Tool result exceeded the per-result character budget and could not be "
                    + "structurally compacted. A leading preview is included; re-call the tool with a "
                    + "narrower filter for complete data."
            },
            ["preview"] = preview
        };
        return envelope.ToJsonString();
    }

    private static ToolResultMetrics Metrics(
        string toolName, int originalChars, int returnedChars, ToolResultBudgetOptions options,
        bool exempt = false, long durationMs = 0) => new()
        {
            ToolName = toolName,
            OriginalChars = originalChars,
            ReturnedChars = returnedChars,
            EstimatedTokens = options.EstimateTokens(returnedChars),
            Exempt = exempt,
            DurationMs = durationMs
        };
}

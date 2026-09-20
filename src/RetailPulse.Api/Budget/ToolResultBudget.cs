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

        // 3) Guaranteed-valid structured degradation envelope if a pathological payload
        //    is still over budget. This never emits a raw JSON prefix of the original
        //    payload — see HardClip for the machine-readable contract (issue #302).
        if (current.Length > maxChars)
        {
            current = HardClip(current, maxChars, toolName);
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
    /// Produces a valid-JSON <b>degradation envelope</b> when structural compaction still
    /// leaves the payload over budget. The envelope is unambiguously machine-readable:
    /// a top-level <c>status="degraded"</c>, an explicit <c>truncated</c>/<c>complete=false</c>
    /// marker, an <c>original</c>/<c>retained</c>/<c>dropped</c> character breakdown, and
    /// structured <c>retry</c> guidance describing concrete narrower-filter / split /
    /// summary-tool strategies the caller (or model) can act on.
    ///
    /// Critically, no field at the top level ever contains a raw JSON prefix of the
    /// original payload — the previous <c>preview</c> field silently returned whatever
    /// regions/rows happened to serialize first, letting the model treat a partial result
    /// as complete (see issue #302). A tiny, clearly-labeled <c>diagnostic_preview.excerpt</c>
    /// string is included only when the per-result budget leaves headroom for it, and only
    /// as an escaped opaque debugging fragment — never as structured data.
    ///
    /// Whether or not the excerpt fits, the envelope itself is bounded and self-describing
    /// so the caller can detect degraded output and cannot mistake it for complete data.
    /// </summary>
    internal static string HardClip(string payload, int maxChars, string? toolName = null)
    {
        payload ??= string.Empty;
        int originalChars = payload.Length;

        // Mandatory contract first (always emit, even if maxChars is smaller than the
        // envelope — the machine-readable degradation signal is more important than
        // strictly honouring a pathologically small budget).
        JsonObject envelope = BuildDegradationEnvelope(
            toolName: toolName,
            originalChars: originalChars,
            retainedChars: 0);

        string baseJson = envelope.ToJsonString();

        // Cap the opaque diagnostic excerpt at a deliberately-tiny window so it can
        // never be large enough to look like a substantive partial result.
        const int diagnosticExcerptCap = 256;

        // Measure the overhead of adding an empty diagnostic_preview object so we can
        // size the excerpt against real remaining budget (JSON escaping can expand
        // arbitrary characters, so we also verify by serialising and shrinking on
        // overflow).
        envelope["diagnostic_preview"] = new JsonObject
        {
            ["note"] = "Opaque debug fragment; NOT the tool result. Do not parse or quote.",
            ["kind"] = "text_fragment",
            ["excerpt_chars"] = 0,
            ["excerpt"] = string.Empty
        };
        int overhead = envelope.ToJsonString().Length - baseJson.Length;

        int excerptBudget = maxChars - baseJson.Length - overhead;
        int excerptLen = Math.Min(diagnosticExcerptCap, Math.Max(0, excerptBudget));
        excerptLen = Math.Min(excerptLen, originalChars);

        if (excerptLen <= 0)
        {
            envelope.Remove("diagnostic_preview");
            return envelope.ToJsonString();
        }

        // Fit-check with actual escaping: JSON string encoding can lengthen the excerpt.
        while (excerptLen > 0)
        {
            envelope["diagnostic_preview"] = new JsonObject
            {
                ["note"] = "Opaque debug fragment; NOT the tool result. Do not parse or quote.",
                ["kind"] = "text_fragment",
                ["excerpt_chars"] = excerptLen,
                ["excerpt"] = payload[..excerptLen]
            };
            string candidate = envelope.ToJsonString();
            if (candidate.Length <= maxChars)
            {
                return candidate;
            }
            // Shrink and retry — a small linear step is safe because excerpt is <=256.
            excerptLen -= Math.Max(1, candidate.Length - maxChars);
        }

        envelope.Remove("diagnostic_preview");
        return envelope.ToJsonString();
    }

    private static JsonObject BuildDegradationEnvelope(
        string? toolName,
        int originalChars,
        int retainedChars)
    {
        var envelope = new JsonObject
        {
            ["status"] = "degraded",
            ["error"] = "tool_result_over_budget",
            ["complete"] = false,
            ["truncated"] = true,
            ["budget"] = new JsonObject
            {
                ["original_chars"] = originalChars,
                ["retained_chars"] = retainedChars,
                ["dropped_chars"] = Math.Max(0, originalChars - retainedChars)
            },
            ["retry"] = new JsonObject
            {
                ["reason"] = "Budget exceeded; dropped content may be needed. Treat as failure, not partial.",
                ["strategies"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["action"] = "narrow_filter",
                        ["description"] = "Re-call with a narrower filter (single region/brand/channel or shorter window)."
                    },
                    new JsonObject
                    {
                        ["action"] = "split_and_aggregate",
                        ["description"] = "Split into smaller disjoint calls and combine results."
                    },
                    new JsonObject
                    {
                        ["action"] = "prefer_summary_tool",
                        ["description"] = "Prefer an aggregate/summary tool over raw-detail."
                    }
                }
            }
        };

        if (!string.IsNullOrEmpty(toolName))
        {
            envelope["tool_name"] = toolName;
        }

        return envelope;
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

namespace RetailPulse.Api.Agents;

/// <summary>
/// A single pre-fetched tool result after it has passed through the tool-context budget
/// boundary, carrying both the (possibly compacted) JSON content and the compaction
/// state needed to emit accurate prompt guidance.
///
/// A <b>complete</b> entry (<see cref="IsSummary"/> is <c>false</c>) is exhaustive — the
/// model should use it directly and not repeat the identical call. A <b>summary</b> entry
/// was rolled up or truncated to fit the budget: it is safe for summary-level answers, but
/// the model may legitimately re-call the same tool with a narrower scope for week-level
/// or other fine-grained detail. This distinction is what lets the system prompt avoid a
/// blanket "do not call these tools again" that would contradict a compactor's own
/// continuation hint.
/// </summary>
/// <param name="ToolName">Name of the tool whose result this is.</param>
/// <param name="Json">The compacted (or original, when complete) JSON payload.</param>
/// <param name="Compacted">True when a tool-specific summarizer reshaped the payload.</param>
/// <param name="Truncated">True when array truncation dropped elements.</param>
/// <param name="OriginalItems">Item count of the dominant array before compaction, when known.</param>
/// <param name="ReturnedItems">Item count retained after compaction, when known.</param>
internal readonly record struct PrefetchEntry(
    string ToolName,
    string Json,
    bool Compacted,
    bool Truncated,
    int? OriginalItems,
    int? ReturnedItems)
{
    /// <summary>
    /// True when the payload was reshaped or truncated to fit the budget, so it is a
    /// summary the model may re-call for detail rather than an exhaustive result.
    /// </summary>
    public bool IsSummary => Compacted || Truncated;

    /// <summary>Builds a complete (uncompacted) entry that carries the payload verbatim.</summary>
    public static PrefetchEntry Complete(string toolName, string json) =>
        new(toolName, json, Compacted: false, Truncated: false, OriginalItems: null, ReturnedItems: null);
}

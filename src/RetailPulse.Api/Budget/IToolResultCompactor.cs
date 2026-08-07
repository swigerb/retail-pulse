namespace RetailPulse.Api.Budget;

/// <summary>
/// Outcome of a tool-specific compaction attempt. <see cref="Json"/> is always valid
/// JSON. When <see cref="Changed"/> is false the compactor did not recognize/handle
/// the payload and the caller should fall back to the generic strategy.
/// </summary>
public readonly record struct ToolCompactionOutcome(
    string Json,
    bool Changed,
    bool Truncated,
    int? OriginalItems,
    int? ReturnedItems)
{
    public static ToolCompactionOutcome Unhandled(string original) =>
        new(original, Changed: false, Truncated: false, OriginalItems: null, ReturnedItems: null);
}

/// <summary>
/// A tool-specific projection/summarizer. Compactors reshape a known tool's verbose
/// result into a compact, still-useful shape (preserving totals and enough aligned
/// points for charts) instead of blunt truncation. Registered compactors are tried in
/// order before the generic array-truncation fallback.
/// </summary>
public interface IToolResultCompactor
{
    /// <summary>Whether this compactor handles the named tool.</summary>
    bool CanCompact(string toolName);

    /// <summary>
    /// Produce a compact JSON projection of <paramref name="rawJson"/>. Must return valid
    /// JSON and must never fabricate data. Return <see cref="ToolCompactionOutcome.Unhandled"/>
    /// when the payload shape is not recognized.
    /// </summary>
    ToolCompactionOutcome Compact(string toolName, string rawJson, ToolResultBudgetOptions options);
}

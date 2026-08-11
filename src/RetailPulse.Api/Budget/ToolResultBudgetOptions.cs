namespace RetailPulse.Api.Budget;

/// <summary>
/// Configuration for the tool-result compaction boundary. Bound from the
/// <c>ToolResultBudget</c> configuration section. Every value has a safe default so
/// the boundary is active even without explicit configuration.
/// </summary>
public sealed class ToolResultBudgetOptions
{
    public const string SectionName = "ToolResultBudget";

    /// <summary>Master switch. When false the boundary passes tool results through unchanged.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum serialized characters allowed for a single tool result before compaction.</summary>
    public int MaxResultChars { get; set; } = 6_000;

    /// <summary>
    /// Maximum cumulative serialized characters of all tool results within one request.
    /// Once exceeded, further tool results are replaced with a compact diagnostic so the
    /// model context cannot explode.
    /// </summary>
    public int MaxCumulativeChars { get; set; } = 24_000;

    /// <summary>Maximum number of distinct (non-deduplicated) tool invocations per request.</summary>
    public int MaxToolCalls { get; set; } = 8;

    /// <summary>
    /// Tighter distinct-call cap that applies when the current request was classified
    /// as an explicit chart-request intent (see <see cref="RequestToolContext.Begin"/>).
    /// Chart intents should never fan out into per-brand tool storms: the aggregate
    /// tools (e.g. <c>GetPortfolioDepletionStats</c>) answer cross-brand ranking in
    /// one call. Enforcing a hard 5-call ceiling here prevents pathological sequential
    /// fan-outs from exhausting the tool-context budget and hallucinating "truncated"
    /// refusal prose.
    /// </summary>
    public int MaxToolCallsForChartIntent { get; set; } = 5;

    /// <summary>Rough characters-per-token divisor used for token estimation and telemetry.</summary>
    public int CharsPerToken { get; set; } = 4;

    /// <summary>Maximum array elements retained by the generic array compactor before truncation.</summary>
    public int MaxArrayItems { get; set; } = 24;

    /// <summary>
    /// Tools whose results must pass through unchanged because they carry a canonical
    /// payload the frontend depends on (e.g. <c>CreateChart</c> returns the renderable
    /// <c>ChartSpec</c>). These are never compacted or truncated.
    /// </summary>
    public HashSet<string> ExemptTools { get; set; } =
        new(StringComparer.OrdinalIgnoreCase) { "CreateChart" };

    /// <summary>Optional per-tool overrides of <see cref="MaxResultChars"/>.</summary>
    public Dictionary<string, int> PerToolMaxResultChars { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public int EstimateTokens(int chars) =>
        (int)Math.Ceiling(chars / (double)Math.Max(1, CharsPerToken));

    public bool IsExempt(string toolName) => ExemptTools.Contains(toolName);

    public int ResolveMaxResultChars(string toolName) =>
        PerToolMaxResultChars.TryGetValue(toolName, out int v) && v > 0 ? v : MaxResultChars;
}

namespace RetailPulse.Api.Budget;

/// <summary>
/// Per-invocation telemetry for a single tool result passing through the budget
/// boundary. Carries only sizes/flags — never payload content or PII — so it is safe
/// to log and surface on the cost dashboard.
/// </summary>
public sealed record ToolResultMetrics
{
    public required string ToolName { get; init; }

    /// <summary>Serialized characters of the raw tool result before compaction.</summary>
    public int OriginalChars { get; init; }

    /// <summary>Serialized characters actually returned to the model context.</summary>
    public int ReturnedChars { get; init; }

    /// <summary>Item count of the dominant array in the raw result, when known.</summary>
    public int? OriginalItems { get; init; }

    /// <summary>Item count retained after compaction, when known.</summary>
    public int? ReturnedItems { get; init; }

    /// <summary>Estimated tokens of the returned payload (ReturnedChars / CharsPerToken).</summary>
    public int EstimatedTokens { get; init; }

    /// <summary>True when a tool-specific summarizer reshaped the payload.</summary>
    public bool Compacted { get; init; }

    /// <summary>True when generic array truncation dropped elements (metadata added).</summary>
    public bool Truncated { get; init; }

    /// <summary>True when this call reused an identical earlier result in the same request.</summary>
    public bool Deduplicated { get; init; }

    /// <summary>True when the tool was on the exempt list and passed through unchanged.</summary>
    public bool Exempt { get; init; }

    /// <summary>True when a per-request budget/cap replaced the result with a diagnostic.</summary>
    public bool BudgetExceeded { get; init; }

    /// <summary>Wall-clock duration of the underlying tool invocation (0 for dedup hits).</summary>
    public long DurationMs { get; init; }
}

namespace RetailPulse.Contracts.Tracing;

/// <summary>
/// A single span within a distributed trace.
/// </summary>
public record TraceSpan(
    string SpanId,
    string TraceId,
    string? ParentSpanId,
    string OperationName,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    double DurationMs,
    int InputTokens = 0,
    int OutputTokens = 0,
    decimal EstimatedCostUsd = 0m,
    IDictionary<string, string>? Tags = null
);

/// <summary>
/// Aggregated usage statistics for a tool across captured trace spans.
/// </summary>
/// <remarks>
/// Deliberately reports time rather than tokens. Tool spans are MCP round trips to the tool
/// host, so they never carry model tokens: those belong to the LLM span that decided to call
/// the tool. A per-tool token total could therefore only ever be zero, which is what the cost
/// dashboard used to show.
/// </remarks>
public record ToolUsageStat(string ToolName, int CallCount, double TotalDurationMs, double AvgDurationMs);

/// <summary>
/// Summary of a complete trace including all spans and aggregated metrics.
/// </summary>
public record TraceSummary(
    string TraceId,
    IReadOnlyList<TraceSpan> Spans,
    double TotalDurationMs,
    int TotalInputTokens,
    int TotalOutputTokens,
    decimal TotalEstimatedCostUsd,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime
);

/// <summary>
/// Structured trace step for Teams card rendering.
/// </summary>
public record TraceStep(
    string Name,
    double DurationMs,
    string? Result = null,
    double? Confidence = null,
    int? ToolsCalled = null,
    TraceTokenDetail? Tokens = null,
    string? ResultSize = null);

/// <summary>
/// Token detail for a single trace step.
/// </summary>
public record TraceTokenDetail(int In, int Out);

/// <summary>
/// Structured trace summary suitable for Teams cards and dashboard display.
/// </summary>
public record StructuredTraceSummary(
    string TraceId,
    double DurationMs,
    List<TraceStep> Steps,
    TraceTokenDetail TotalTokens,
    decimal EstimatedCostUsd);

/// <summary>
/// Collects and stores distributed trace spans in a ring buffer.
/// Thread-safe for concurrent span capture from multiple agents.
/// Ring buffer capacity defaults to 100 traces.
/// </summary>
public interface ITraceCollector
{
    /// <summary>
    /// Captures a completed span. Thread-safe.
    /// </summary>
    void CaptureSpan(TraceSpan span);

    /// <summary>
    /// Returns all spans for a specific trace ID, ordered by start time.
    /// Returns null if the trace ID is not found.
    /// </summary>
    IReadOnlyList<TraceSpan>? GetSpans(string traceId);

    /// <summary>
    /// Builds a summary for a trace, including aggregated token counts and costs.
    /// Returns null if the trace ID is not found.
    /// </summary>
    TraceSummary? GetSummary(string traceId);

    /// <summary>
    /// Returns the most recent traces, ordered by most recent first.
    /// </summary>
    IReadOnlyList<TraceSummary> GetRecentTraces(int count = 20);

    /// <summary>
    /// Builds a structured summary suitable for Teams cards.
    /// Returns null if the trace ID is not found.
    /// </summary>
    StructuredTraceSummary? GetStructuredSummary(string traceId);

    /// <summary>
    /// Returns aggregated tool usage stats for tool spans captured since the cutoff.
    /// </summary>
    IReadOnlyList<ToolUsageStat> GetToolStats(DateTimeOffset since, int top = 10);

    /// <summary>
    /// Current number of traces stored in the ring buffer.
    /// </summary>
    int TraceCount { get; }

    /// <summary>
    /// Maximum number of traces the ring buffer can hold.
    /// </summary>
    int Capacity { get; }
}

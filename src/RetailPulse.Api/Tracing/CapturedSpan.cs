namespace RetailPulse.Api.Tracing;

/// <summary>
/// A captured OTel span from the agent pipeline, used to bridge
/// System.Diagnostics.Activity data into the trace collector.
/// </summary>
public record CapturedSpan(
    string SpanId,
    string TraceId,
    string? ParentSpanId,
    string OperationName,
    string Kind,
    long DurationMs,
    DateTime StartTime,
    DateTime EndTime,
    IDictionary<string, object?> Attributes
);

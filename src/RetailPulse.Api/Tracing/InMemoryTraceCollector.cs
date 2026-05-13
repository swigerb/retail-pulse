using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using RetailPulse.Api.Hubs;
using RetailPulse.Contracts.Tracing;

namespace RetailPulse.Api.Tracing;

/// <summary>
/// In-memory trace collector with ring buffer eviction and SignalR push.
/// Thread-safe for concurrent span capture.
/// Default capacity: 100 traces.
/// </summary>
public class InMemoryTraceCollector : ITraceCollector
{
    private readonly ConcurrentDictionary<string, ConcurrentBag<TraceSpan>> _traces = new();
    private readonly ConcurrentQueue<string> _traceOrder = new();
    private readonly object _evictionLock = new();
    private readonly IHubContext<TelemetryHub>? _hubContext;
    private readonly IConfiguration? _configuration;

    // Default gpt-5.4-mini pricing per million tokens
    private const decimal DefaultInputPricePerMillion = 0.15m;
    private const decimal DefaultOutputPricePerMillion = 0.60m;

    public int Capacity { get; }

    public InMemoryTraceCollector(int capacity = 100)
    {
        Capacity = capacity;
    }

    public InMemoryTraceCollector(
        IHubContext<TelemetryHub> hubContext,
        IConfiguration configuration,
        int capacity = 100)
    {
        _hubContext = hubContext;
        _configuration = configuration;
        Capacity = capacity;
    }

    public int TraceCount => _traces.Count;

    public void CaptureSpan(TraceSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);

        var bag = _traces.GetOrAdd(span.TraceId, traceId =>
        {
            // Track insertion order for ring buffer eviction
            _traceOrder.Enqueue(traceId);

            // Fire-and-forget: notify clients of new trace
            Task.Run(() => NotifyTraceStartedAsync(traceId, span.StartTime));
            return new ConcurrentBag<TraceSpan>();
        });

        bag.Add(span);

        // Fire-and-forget: notify clients of completed span
        _ = NotifySpanCompletedAsync(span);

        // Ring buffer eviction
        EvictIfNeeded();
    }

    public IReadOnlyList<TraceSpan>? GetSpans(string traceId)
    {
        if (!_traces.TryGetValue(traceId, out var bag))
            return null;

        return bag.OrderBy(s => s.StartTime).ToList();
    }

    public TraceSummary? GetSummary(string traceId)
    {
        var spans = GetSpans(traceId);
        if (spans == null || spans.Count == 0)
            return null;

        var totalInputTokens = spans.Sum(s => s.InputTokens);
        var totalOutputTokens = spans.Sum(s => s.OutputTokens);
        var totalCost = spans.Sum(s => s.EstimatedCostUsd);
        var startTime = spans.Min(s => s.StartTime);
        var endTime = spans.Max(s => s.EndTime);
        var totalDurationMs = (endTime - startTime).TotalMilliseconds;

        return new TraceSummary(
            TraceId: traceId,
            Spans: spans,
            TotalDurationMs: totalDurationMs,
            TotalInputTokens: totalInputTokens,
            TotalOutputTokens: totalOutputTokens,
            TotalEstimatedCostUsd: totalCost,
            StartTime: startTime,
            EndTime: endTime
        );
    }

    public StructuredTraceSummary? GetStructuredSummary(string traceId)
    {
        var spans = GetSpans(traceId);
        if (spans == null || spans.Count == 0)
            return null;

        var totalInputTokens = spans.Sum(s => s.InputTokens);
        var totalOutputTokens = spans.Sum(s => s.OutputTokens);
        var startTime = spans.Min(s => s.StartTime);
        var endTime = spans.Max(s => s.EndTime);
        var totalDurationMs = (endTime - startTime).TotalMilliseconds;
        var totalCost = CalculateCost(totalInputTokens, totalOutputTokens);

        var steps = new List<TraceStep>();
        foreach (var span in spans)
        {
            var tags = span.Tags ?? new Dictionary<string, string>();

            var step = span.OperationName switch
            {
                "router.classify" => new TraceStep(
                    "Intent Classification",
                    span.DurationMs,
                    Result: GetTag(tags, "router.intent"),
                    Confidence: double.TryParse(GetTag(tags, "router.confidence"), out var c) ? c : null),

                "router.select_agent" => new TraceStep(
                    $"Agent Selection: {GetTag(tags, "router.selected_agent") ?? "unknown"}",
                    span.DurationMs,
                    Result: GetTag(tags, "router.selected_agent")),

                var op when op.StartsWith("agent.") && op.EndsWith(".process") => new TraceStep(
                    $"Agent: {GetTag(tags, "agent.name") ?? op}",
                    span.DurationMs,
                    ToolsCalled: int.TryParse(GetTag(tags, "agent.tools_called_count"), out var tc) ? tc : null,
                    Tokens: span.InputTokens > 0 || span.OutputTokens > 0
                        ? new TraceTokenDetail(span.InputTokens, span.OutputTokens) : null),

                var op when op.StartsWith("tool.") => new TraceStep(
                    $"Tool: {GetTag(tags, "tool.name") ?? op}",
                    span.DurationMs,
                    ResultSize: GetTag(tags, "tool.result_size")),

                var op when op.StartsWith("memory.") => new TraceStep(
                    $"Memory: {(op.Contains("recall") ? "recalled" : "stored")} {GetTag(tags, "memory.entries_recalled") ?? GetTag(tags, "memory.entries_stored") ?? ""}".Trim(),
                    span.DurationMs),

                var op when op.StartsWith("approval.") => new TraceStep(
                    $"Approval: {(op.Contains("request") ? "requested" : "waiting")}",
                    span.DurationMs,
                    Result: GetTag(tags, "approval.decision")),

                _ => new TraceStep(span.OperationName, span.DurationMs)
            };

            steps.Add(step);
        }

        return new StructuredTraceSummary(
            traceId,
            totalDurationMs,
            steps,
            new TraceTokenDetail(totalInputTokens, totalOutputTokens),
            totalCost);
    }

    public IReadOnlyList<TraceSummary> GetRecentTraces(int count = 20)
    {
        var traceIds = _traceOrder.ToArray();
        var summaries = new List<TraceSummary>();

        // Iterate in reverse order (most recent first)
        for (int i = traceIds.Length - 1; i >= 0 && summaries.Count < count; i--)
        {
            var summary = GetSummary(traceIds[i]);
            if (summary != null)
                summaries.Add(summary);
        }

        return summaries;
    }

    /// <summary>
    /// Notifies SignalR clients that a trace is complete. Call after all spans
    /// for a request have been captured.
    /// </summary>
    public async Task NotifyTraceCompletedAsync(string traceId)
    {
        if (_hubContext is null) return;

        var summary = GetStructuredSummary(traceId);
        if (summary is not null)
        {
            await _hubContext.Clients.All.SendAsync("trace_completed", summary);
        }
    }

    private async Task NotifyTraceStartedAsync(string traceId, DateTimeOffset timestamp)
    {
        if (_hubContext is null) return;

        try
        {
            await _hubContext.Clients.All.SendAsync("trace_started", new
            {
                traceId,
                timestamp
            });
        }
        catch
        {
            // SignalR push is best-effort — don't break span capture
        }
    }

    private async Task NotifySpanCompletedAsync(TraceSpan span)
    {
        if (_hubContext is null) return;

        try
        {
            await _hubContext.Clients.All.SendAsync("span_completed", new
            {
                span.TraceId,
                span.SpanId,
                span.OperationName,
                span.DurationMs,
                span.StartTime,
                span.EndTime,
                span.InputTokens,
                span.OutputTokens,
                span.Tags
            });
        }
        catch
        {
            // SignalR push is best-effort — don't break span capture
        }
    }

    private decimal CalculateCost(int inputTokens, int outputTokens)
    {
        decimal inputRate = DefaultInputPricePerMillion;
        decimal outputRate = DefaultOutputPricePerMillion;

        if (_configuration is not null)
        {
            var pricingSection = _configuration.GetSection("TokenPricing");
            foreach (var model in pricingSection.GetChildren())
            {
                var inRate = model.GetValue<decimal?>("InputPerMillion");
                var outRate = model.GetValue<decimal?>("OutputPerMillion");
                if (inRate.HasValue && outRate.HasValue)
                {
                    inputRate = inRate.Value;
                    outputRate = outRate.Value;
                    break;
                }
            }
        }

        return (inputTokens * inputRate / 1_000_000m) + (outputTokens * outputRate / 1_000_000m);
    }

    private void EvictIfNeeded()
    {
        lock (_evictionLock)
        {
            while (_traces.Count > Capacity && _traceOrder.TryDequeue(out var oldest))
            {
                _traces.TryRemove(oldest, out _);
            }
        }
    }

    private static string? GetTag(IDictionary<string, string> tags, string key)
        => tags.TryGetValue(key, out var value) ? value : null;
}

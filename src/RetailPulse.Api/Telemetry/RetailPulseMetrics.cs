using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RetailPulse.Api.Telemetry;

/// <summary>
/// Custom business metrics for RetailPulse using System.Diagnostics.Metrics.
/// Registered as a singleton and injected where instrumentation is needed.
/// </summary>
public sealed class RetailPulseMetrics
{
    public const string MeterName = "RetailPulse";

    private readonly Meter _meter;

    // Counters
    private readonly Counter<long> _intentClassificationTotal;
    private readonly Counter<long> _cacheHitTotal;
    private readonly Counter<long> _cacheMissTotal;
    private readonly Counter<long> _errorTotal;

    // Histograms
    private readonly Histogram<double> _toolCallDurationMs;
    private readonly Histogram<double> _agentExecutionDurationMs;
    private readonly Histogram<double> _routingDurationMs;

    // SLI counters
    private readonly Counter<long> _requestTotal;
    private readonly Histogram<double> _requestDurationMs;

    public RetailPulseMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName);

        _intentClassificationTotal = _meter.CreateCounter<long>(
            "retailpulse.intent_classification_total",
            description: "Total intent classifications performed");

        _cacheHitTotal = _meter.CreateCounter<long>(
            "retailpulse.cache_hit_total",
            description: "Total cache hits");

        _cacheMissTotal = _meter.CreateCounter<long>(
            "retailpulse.cache_miss_total",
            description: "Total cache misses");

        _errorTotal = _meter.CreateCounter<long>(
            "retailpulse.error_total",
            description: "Total errors by category");

        _toolCallDurationMs = _meter.CreateHistogram<double>(
            "retailpulse.tool_call_duration_ms",
            unit: "ms",
            description: "Duration of individual tool calls");

        _agentExecutionDurationMs = _meter.CreateHistogram<double>(
            "retailpulse.agent_execution_duration_ms",
            unit: "ms",
            description: "End-to-end agent execution duration");

        _routingDurationMs = _meter.CreateHistogram<double>(
            "retailpulse.routing_duration_ms",
            unit: "ms",
            description: "Time spent in intent routing/classification");

        _requestTotal = _meter.CreateCounter<long>(
            "retailpulse.request_total",
            description: "Total requests processed (SLI)");

        _requestDurationMs = _meter.CreateHistogram<double>(
            "retailpulse.request_duration_ms",
            unit: "ms",
            description: "Overall request duration (SLI)");
    }

    public void RecordIntentClassification(string intent, bool fastPathHit)
    {
        var tags = new TagList
        {
            { "intent", intent },
            { "fast_path_hit", fastPathHit }
        };
        _intentClassificationTotal.Add(1, tags);
    }

    public void RecordCacheHit() => _cacheHitTotal.Add(1);

    public void RecordCacheMiss() => _cacheMissTotal.Add(1);

    public void RecordError(string category) => _errorTotal.Add(1, new TagList { { "category", category } });

    public void RecordToolCallDuration(string toolName, double durationMs) => _toolCallDurationMs.Record(durationMs, new TagList { { "tool_name", toolName } });

    public void RecordAgentExecutionDuration(string agentKey, double durationMs) => _agentExecutionDurationMs.Record(durationMs, new TagList { { "agent_key", agentKey } });

    public void RecordRoutingDuration(double durationMs) => _routingDurationMs.Record(durationMs);

    public void RecordRequest(double durationMs, bool isError)
    {
        _requestTotal.Add(1, new TagList { { "is_error", isError } });
        _requestDurationMs.Record(durationMs);
    }
}

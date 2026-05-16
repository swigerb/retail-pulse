using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace RetailPulse.Api.Explainability;

/// <summary>
/// Captures tool execution traces and produces explanation chains for transparency.
/// Supports "why?" queries by exposing the reasoning steps and data sources behind
/// each agent response.
/// </summary>
public class ExplainabilityService
{
    private readonly ILogger<ExplainabilityService> _logger;
    private readonly ConcurrentDictionary<string, ExplanationTrace> _traces = new();

    public ExplainabilityService(ILogger<ExplainabilityService> logger)
    {
        _logger = logger;
    }

    public record ToolStep(
        string ToolName,
        string Arguments,
        string ResultSummary,
        long DurationMs,
        DateTime Timestamp);

    public record ReasoningStep(
        string AgentKey,
        string Phase,
        string Description,
        DateTime Timestamp);

    public record ExplanationTrace(
        string SessionId,
        string Query,
        List<ToolStep> ToolSteps,
        List<ReasoningStep> ReasoningChain,
        string? FinalAnswer,
        DateTime StartedAt,
        long TotalDurationMs)
    {
        public ToolStep[] DataSources => [.. ToolSteps];
        public int ToolCallCount => ToolSteps.Count;
    }

    /// <summary>Start tracing a new query.</summary>
    public string StartTrace(string sessionId, string query)
    {
        string traceId = $"{sessionId}-{Guid.NewGuid():N}"[..32];
        var trace = new ExplanationTrace(
            sessionId, query,
            [], [],
            null, DateTime.UtcNow, 0);

        _traces[traceId] = trace;
        _logger.LogDebug("Started trace {TraceId} for session {Session}", traceId, sessionId);
        return traceId;
    }

    /// <summary>Record a tool invocation step.</summary>
    public void RecordToolCall(string traceId, string toolName, string arguments,
        string resultSummary, long durationMs)
    {
        if (_traces.TryGetValue(traceId, out ExplanationTrace? trace))
        {
            trace.ToolSteps.Add(new ToolStep(
                toolName, arguments,
                resultSummary[..Math.Min(500, resultSummary.Length)],
                durationMs, DateTime.UtcNow));
        }
    }

    /// <summary>Record a reasoning/processing step.</summary>
    public void RecordReasoning(string traceId, string agentKey, string phase, string description)
    {
        if (_traces.TryGetValue(traceId, out ExplanationTrace? trace))
        {
            trace.ReasoningChain.Add(new ReasoningStep(
                agentKey, phase, description, DateTime.UtcNow));
        }
    }

    /// <summary>Complete a trace with the final answer.</summary>
    public void CompleteTrace(string traceId, string finalAnswer, long totalDurationMs)
    {
        if (_traces.TryGetValue(traceId, out ExplanationTrace? existing))
        {
            _traces[traceId] = existing with
            {
                FinalAnswer = finalAnswer,
                TotalDurationMs = totalDurationMs
            };

            _logger.LogDebug(
                "Completed trace {TraceId}: {ToolCount} tool calls, {ReasoningCount} steps, {Ms}ms",
                traceId, existing.ToolSteps.Count, existing.ReasoningChain.Count, totalDurationMs);
        }
    }

    /// <summary>Get a trace by ID.</summary>
    public ExplanationTrace? GetTrace(string traceId)
    {
        _traces.TryGetValue(traceId, out ExplanationTrace? trace);
        return trace;
    }

    /// <summary>Get all traces for a session.</summary>
    public IReadOnlyList<ExplanationTrace> GetSessionTraces(string sessionId)
    {
        return [.. _traces.Values
            .Where(t => t.SessionId == sessionId)
            .OrderByDescending(t => t.StartedAt)];
    }

    /// <summary>
    /// Build a human-readable explanation for why the agent gave a particular answer.
    /// Used for "why?" follow-up queries.
    /// </summary>
    public string BuildExplanation(string traceId)
    {
        if (!_traces.TryGetValue(traceId, out ExplanationTrace? trace))
            return "No trace found for this response.";

        var lines = new List<string>
        {
            $"## How I arrived at this answer",
            $"**Query:** {trace.Query}",
            ""
        };

        if (trace.ToolSteps.Count > 0)
        {
            lines.Add("### Data Sources Consulted");
            for (int i = 0; i < trace.ToolSteps.Count; i++)
            {
                ToolStep step = trace.ToolSteps[i];
                lines.Add($"{i + 1}. **{step.ToolName}** ({step.DurationMs}ms)");
                lines.Add($"   - Input: `{step.Arguments}`");
                lines.Add($"   - Result: {step.ResultSummary[..Math.Min(150, step.ResultSummary.Length)]}");
            }
            lines.Add("");
        }

        if (trace.ReasoningChain.Count > 0)
        {
            lines.Add("### Reasoning Steps");
            foreach (ReasoningStep step in trace.ReasoningChain)
            {
                lines.Add($"- [{step.AgentKey}/{step.Phase}] {step.Description}");
            }
            lines.Add("");
        }

        lines.Add($"**Total processing time:** {trace.TotalDurationMs}ms");
        lines.Add($"**Tool calls made:** {trace.ToolCallCount}");

        return string.Join("\n", lines);
    }

    /// <summary>Prune old traces to prevent unbounded memory growth.</summary>
    public void PruneOlderThan(TimeSpan maxAge)
    {
        DateTime cutoff = DateTime.UtcNow - maxAge;
        var toRemove = _traces.Where(kv => kv.Value.StartedAt < cutoff)
            .Select(kv => kv.Key).ToList();

        foreach (string? key in toRemove)
            _traces.TryRemove(key, out _);

        if (toRemove.Count > 0)
            _logger.LogDebug("Pruned {Count} old traces", toRemove.Count);
    }
}

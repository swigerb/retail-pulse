using Microsoft.Extensions.AI;
using RetailPulse.Api.Charts;
using RetailPulse.Contracts;

namespace RetailPulse.Api.Agents;

/// <summary>
/// Chart-fulfillment invariant for the execution pipeline.
///
/// When the user <b>explicitly</b> asked for a chart, the response must either carry a
/// renderable chart or state — precisely and structurally — that the chart is unavailable
/// because of missing data. It must never silently return prose-only while implying
/// success (the exact P0 failure mode: the model narrates a comparison but emits no chart).
///
/// Enforcement is deterministic and bounded: if the model produced no chart, we first try
/// to reconstruct one from the tool results already captured this turn (no extra LLM call),
/// and only if that is impossible do we append a structured chart-unavailable diagnostic.
/// For non-chart prompts this is a no-op — charts are never forced.
/// </summary>
public partial class AgentExecutionPipeline
{
    internal readonly record struct ChartFulfillmentResult(List<ChartSpec> Charts, string Reply);

    internal ChartFulfillmentResult EnforceChartFulfillment(
        string? userMessage,
        Microsoft.Extensions.AI.ChatResponse response,
        List<ChartSpec> charts,
        string reply)
    {
        ChartIntent intent = ChartRequestDetector.Detect(userMessage);

        // Not an explicit chart request → never force a chart.
        if (!intent.IsExplicitChartRequest)
        {
            return new ChartFulfillmentResult(charts, reply);
        }

        // Already fulfilled by the model / inline recovery.
        if (charts.Count > 0)
        {
            return new ChartFulfillmentResult(charts, reply);
        }

        // Deterministic, no-LLM reconstruction from this turn's tool results. For a
        // horizontal-bar ranking ask we raise the minimum-marks floor to the P0
        // contract (>= 6 finite marks, at least one non-zero) so an underpopulated
        // or all-zero result FAILS CLOSED to the chart-unavailable diagnostic below
        // rather than reaching the frontend as an empty shell.
        int minMarks = IsPortfolioRankingIntent(userMessage, intent)
            ? Math.Max(6, ChartSpecValidator.MinimumMarksForType(intent.ChartType))
            : ChartSpecValidator.MinimumMarksForType(intent.ChartType);

        if (DeterministicChartBuilder.TryBuild(response, intent.ChartType, minMarks, out ChartSpec? built) && built is not null)
        {
            _logger.LogInformation(
                "Chart-fulfillment: reconstructed a {ChartType} chart deterministically from tool results "
                + "for an explicit chart request that returned prose-only.",
                built.Type);
            charts.Add(built);
            return new ChartFulfillmentResult(charts, reply);
        }

        // No renderable chart and no data to build one — surface a precise, structured
        // diagnostic instead of a silent prose-only reply.
        _logger.LogWarning(
            "Chart-fulfillment: explicit {ChartType} chart request could not be satisfied — "
            + "no renderable chart and no chartable tool data present; emitting chart-unavailable diagnostic.",
            intent.ChartType ?? "chart");

        string diagnostic = BuildChartUnavailableDiagnostic(intent.ChartType);
        string updatedReply = string.IsNullOrWhiteSpace(reply)
            ? diagnostic
            : $"{reply}\n\n{diagnostic}";

        return new ChartFulfillmentResult(charts, updatedReply);
    }

    private static string BuildChartUnavailableDiagnostic(string? chartType)
    {
        string kind = string.IsNullOrWhiteSpace(chartType) ? "chart" : $"{chartType} chart";
        return $"⚠️ Chart unavailable: I could not render the requested {kind} because the "
            + "underlying data tools returned no chartable values for this request. This is a "
            + "data-availability issue, not a rendering failure — please retry with a specific "
            + "brand and region, or confirm the entity exists for this tenant.";
    }

    /// <summary>
    /// True when the explicit chart request is asking for a portfolio ranking / growth
    /// comparison across brands. Intent-shape only (no brand/tenant literals) so this
    /// generalises to any tenant. When true, the fulfillment path enforces a stricter
    /// minimum-marks floor (>= 6 finite marks with at least one non-zero) so a chart
    /// of zeros or a velocity fallback can never surface as a "growth ranking".
    /// </summary>
    private static bool IsPortfolioRankingIntent(string? userMessage, ChartIntent intent)
    {
        if (!string.Equals(intent.ChartType, "horizontalBar", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        string m = userMessage;
        string[] rankingCues =
        [
            "rank all brands", "ranking all brands", "rank brands", "brands ranked",
            "growth rate", "yoy growth", "year-over-year growth", "year over year growth",
            "top brands", "fastest growing", "fastest-growing",
            "portfolio ranking", "all brands by", "compare all brands",
            "brand ranking", "cross-brand ranking",
        ];
        foreach (string cue in rankingCues)
        {
            if (m.Contains(cue, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

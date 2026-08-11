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

        bool isPortfolioRanking = IsPortfolioRankingIntent(userMessage, intent);

        // Portfolio-ranking coverage invariant: when the user asked for a ranking of
        // ALL tenant brands and the tenant roster is available, the answer MUST cover
        // every brand. The aggregate tool payload is the source of truth; a
        // model-emitted chart that silently drops half the portfolio is treated as
        // non-fulfilling and replaced with the deterministic ranking built from the
        // tool payload (which the compactor preserves in full). This is tenant-generic
        // — driven by tenant.yaml, no brand or count literals.
        IReadOnlyCollection<string>? roster = isPortfolioRanking && _tenant.Brands.Count > 0
            ? _tenant.Brands.Select(b => b.Name).ToArray()
            : null;

        if (roster is { Count: > 0 })
        {
            bool alreadyCovers = charts.Any(c =>
                string.Equals(c?.Type, "horizontalBar", StringComparison.OrdinalIgnoreCase)
                && DeterministicChartBuilder.CoversRoster(c, roster));

            if (!alreadyCovers)
            {
                int minMarks = Math.Max(6, roster.Count);
                if (DeterministicChartBuilder.TryBuild(response, intent.ChartType, minMarks, roster, out ChartSpec? rebuilt)
                    && rebuilt is not null)
                {
                    _logger.LogInformation(
                        "Chart-fulfillment: replacing model-emitted horizontalBar with deterministic "
                        + "portfolio ranking covering all {BrandCount} tenant brands.",
                        roster.Count);
                    // Drop any prior horizontalBar model chart(s) — the deterministic
                    // roster-complete chart is the source of truth for this intent.
                    charts.RemoveAll(c => string.Equals(c?.Type, "horizontalBar", StringComparison.OrdinalIgnoreCase));
                    charts.Add(rebuilt);
                    return new ChartFulfillmentResult(charts, StripFallbackClaims(reply));
                }

                // Coverage impossible from the current tool payload — fail closed with a
                // diagnostic listing exactly which brands are missing, and drop any
                // partial model chart so the user is not silently misled.
                IReadOnlyList<string> missing = ComputeMissingBrands(charts, roster);
                _logger.LogWarning(
                    "Chart-fulfillment: portfolio ranking missing {Missing} brand(s) — failing closed.",
                    missing.Count);
                charts.RemoveAll(c => string.Equals(c?.Type, "horizontalBar", StringComparison.OrdinalIgnoreCase));
                string diag = BuildRankingCoverageDiagnostic(missing, roster.Count);
                string updated = string.IsNullOrWhiteSpace(reply) ? diag : $"{reply}\n\n{diag}";
                return new ChartFulfillmentResult(charts, updated);
            }
        }

        // Already fulfilled by the model / inline recovery.
        if (charts.Count > 0)
        {
            // If a roster-complete portfolio ranking chart is present, scrub any
            // fallback/truncation vocabulary the model may have narrated into the
            // prose (issue #74) — the chart is authoritative and the prose must
            // not undermine it.
            string sanitizedReply = (roster is { Count: > 0 } && charts.Any(c =>
                    string.Equals(c?.Type, "horizontalBar", StringComparison.OrdinalIgnoreCase)
                    && DeterministicChartBuilder.CoversRoster(c, roster)))
                ? StripFallbackClaims(reply)
                : reply;
            return new ChartFulfillmentResult(charts, sanitizedReply);
        }

        // Deterministic, no-LLM reconstruction from this turn's tool results. For a
        // horizontal-bar ranking ask we raise the minimum-marks floor to the P0
        // contract (>= 6 finite marks, at least one non-zero) so an underpopulated
        // or all-zero result FAILS CLOSED to the chart-unavailable diagnostic below
        // rather than reaching the frontend as an empty shell.
        int fallbackMinMarks = isPortfolioRanking
            ? Math.Max(6, ChartSpecValidator.MinimumMarksForType(intent.ChartType))
            : ChartSpecValidator.MinimumMarksForType(intent.ChartType);

        if (DeterministicChartBuilder.TryBuild(response, intent.ChartType, fallbackMinMarks, out ChartSpec? built) && built is not null)
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

    private static IReadOnlyList<string> ComputeMissingBrands(
        IReadOnlyList<ChartSpec> charts,
        IReadOnlyCollection<string> roster)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartSpec chart in charts)
        {
            if (chart is null) continue;
            if (!string.Equals(chart.Type, "horizontalBar", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (ChartSeries s in chart.Data)
            {
                foreach (ChartDataPoint p in s.Values)
                {
                    if (p?.X is not null && double.IsFinite(p.Y))
                        present.Add(p.X);
                }
            }
        }
        return [.. roster.Where(b => !present.Contains(b))];
    }

    private static string BuildRankingCoverageDiagnostic(IReadOnlyList<string> missing, int rosterCount)
    {
        string list = missing.Count == 0
            ? "one or more portfolio brands"
            : string.Join(", ", missing);
        return $"⚠️ Chart unavailable: a portfolio ranking must cover every configured brand "
            + $"({rosterCount} total for this tenant), but the following were not returned by the "
            + $"underlying data tools: {list}. This is a data-availability issue, not a rendering "
            + "failure — a partial ranking would silently mis-rank the portfolio, so no chart is emitted.";
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

    /// <summary>
    /// Removes lines from the model's prose that contain fallback/truncation
    /// vocabulary when a valid roster-complete chart was in fact produced.
    /// The chart is authoritative; leaving hallucinated "truncated / fallback /
    /// placeholder / should not be used" language in the final assistant message
    /// undermines it and is the exact P0 regression for issue #74 (Publix
    /// production failure #2). Whole sentences containing any banned token are
    /// dropped; if the entire reply is fallback narrative, a neutral confirmation
    /// is substituted so the chart is not orphaned.
    /// </summary>
    internal static string StripFallbackClaims(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return "Here is the requested portfolio ranking across all configured tenant brands.";
        }

        string[] lines = reply.Split('\n');
        var kept = new List<string>(lines.Length);
        bool anyStripped = false;
        foreach (string rawLine in lines)
        {
            bool banned = false;
            foreach (string phrase in _fallbackClaimVocabulary)
            {
                if (rawLine.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                {
                    banned = true;
                    break;
                }
            }
            if (banned)
            {
                anyStripped = true;
                continue;
            }
            kept.Add(rawLine);
        }

        string cleaned = string.Join('\n', kept).Trim();
        return string.IsNullOrWhiteSpace(cleaned)
            ? "Here is the requested portfolio ranking across all configured tenant brands."
            : anyStripped ? cleaned : reply;
    }
}

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

    /// <summary>
    /// Structural chart-type families. Members share the same underlying data shape
    /// (series-of-{X,Y}) so within-family differences are presentation-only rendering
    /// hints (orientation / grouping / stacking). Cross-family differences imply a
    /// different data model and are treated as an explicit chart-type mismatch — never
    /// silently rewritten. The nine canonical <see cref="ChartSpec.Type"/> values are:
    /// <c>bar, horizontalBar, groupedBar, stackedBar, line, pie, donut, gauge, table</c>.
    /// Gauge and table are singletons because their data shapes (single-scalar; row
    /// grid) don't bind to any other chart type. See issue #76 Group D.
    /// </summary>
    private static readonly string[][] _chartTypeFamilies =
    [
        ["bar", "horizontalBar", "groupedBar", "stackedBar"],
        ["line"],
        ["pie", "donut"],
        ["gauge"],
        ["table"],
    ];

    private static string[]? ChartTypeFamily(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        foreach (string[] family in _chartTypeFamilies)
        {
            foreach (string member in family)
            {
                if (string.Equals(member, type, StringComparison.OrdinalIgnoreCase))
                    return family;
            }
        }
        return null;
    }

    internal ChartFulfillmentResult EnforceChartFulfillment(
        string? userMessage,
        Microsoft.Extensions.AI.ChatResponse response,
        List<ChartSpec> charts,
        string reply)
    {
        ChartIntent intent = ChartRequestDetector.Detect(userMessage);

        // ── GROUP A (issue #76): inverse chart-on-prose invariant ─────────────
        // When the request is NOT an explicit chart request, any model-emitted
        // chart is unsolicited noise — the prose the user actually asked for is
        // the source of truth. Drop every chart before the response leaves the
        // pipeline. Design decision (issue #76): NO prose intent carries a
        // legitimate chart exception. The nine curated chart prompts all pass
        // through ChartRequestDetector as explicit; anything the detector
        // classifies as prose is prose, full stop. This closes the exact P0
        // failure mode where "How is the portfolio performing?" surfaced a
        // model-emitted chart that the user had not asked for.
        if (!intent.IsExplicitChartRequest)
        {
            if (charts.Count > 0)
            {
                _logger.LogInformation(
                    "Chart-fulfillment: dropping {ChartCount} model-emitted chart(s) — the user "
                    + "did not ask for a visualization (chart-on-prose invariant, issue #76 Group A).",
                    charts.Count);
                charts.Clear();
            }
            return new ChartFulfillmentResult(charts, reply);
        }

        // ── GROUP D (issue #76): family-aware chart-type enforcement ──────────
        // ChartRequestDetector.Detect() captured the requested type. Validate
        // every model-emitted chart against that request:
        //   * exact-type match  → keep as-is;
        //   * same family       → coerce Type in place (identical data shape,
        //     presentation-only rewrite is safe and deterministic);
        //   * cross family      → fail closed with a structured diagnostic —
        //     never a silent rewrite (data would not bind, and the user's
        //     stated intent was structurally different).
        // Undeclared type intent (intent.ChartType is null) → no enforcement.
        if (!string.IsNullOrWhiteSpace(intent.ChartType) && charts.Count > 0)
        {
            ChartTypeEnforcementResult typeCheck = EnforceRequestedChartType(charts, intent.ChartType);
            charts = typeCheck.Charts;
            if (typeCheck.CrossFamilyMismatch is { } mismatch)
            {
                _logger.LogWarning(
                    "Chart-fulfillment: model emitted '{ModelType}' but the user asked for "
                    + "'{RequestedType}' (different structural family). Failing closed with a "
                    + "chart-type mismatch diagnostic — issue #76 Group D.",
                    mismatch.ModelType, mismatch.RequestedType);
                string mismatchDiag = BuildChartTypeMismatchDiagnostic(mismatch.ModelType, mismatch.RequestedType);
                string mismatchScrubbed = StripFallbackClaims(reply);
                string mismatchReply = string.IsNullOrWhiteSpace(mismatchScrubbed)
                    ? mismatchDiag
                    : $"{mismatchScrubbed}\n\n{mismatchDiag}";
                return new ChartFulfillmentResult(charts, mismatchReply);
            }
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
                // Scrub the model's fallback/truncation narrative from the prose so
                // the user-visible reply cannot claim a chart was produced when we
                // are in fact failing closed (issue #74 P0 failure #2).
                string scrubbed = StripFallbackClaims(reply);
                string updated = string.IsNullOrWhiteSpace(scrubbed) ? diag : $"{scrubbed}\n\n{diag}";
                return new ChartFulfillmentResult(charts, updated);
            }
        }

        // ── Deterministic-first chart construction (issue #172) ──────────────
        // The tool payload — not the model's improvisation — is the source of
        // truth for an explicit chart request. Build from it whenever we can and
        // prefer that chart over whatever the model emitted.
        //
        // This generalises the portfolio-ranking coverage contract above to every
        // chart intent, and that precedent is the evidence for it: the ranking
        // prompt was the ONLY curated chart that rendered correctly on every live
        // run precisely because its chart was rebuilt from tool data, while the
        // eight prompts left to model discretion drifted between runs — wrong
        // type, too few marks, or the chart JSON narrated into the prose.
        //
        // The model still writes the words; the code draws the chart.
        int requiredMarks = isPortfolioRanking
            ? Math.Max(6, ChartSpecValidator.MinimumMarksForType(intent.ChartType))
            : ChartSpecValidator.MinimumMarksForType(intent.ChartType);

        // A curated prompt carries its own acceptance floor from the chart manifest.
        // Enforcing it here means the live pipeline holds a chart to the SAME contract
        // the acceptance tests assert, rather than the looser "is it renderable at all"
        // per-type minimum — e.g. the two-brand region comparison needs 4 marks, where
        // the generic groupedBar floor is only 2.
        requiredMarks = Math.Max(requiredMarks, intent.MinMarks);

        if (DeterministicChartBuilder.TryBuild(response, intent.ChartType, requiredMarks, out ChartSpec? deterministic)
            && deterministic is not null)
        {
            // Prefer the deterministic chart, but never at the cost of completeness.
            // When the model also produced a valid chart of the requested type that
            // covers MORE of the data, keeping the thinner rebuild would be a
            // regression — the tool payload is authoritative about what is true, not
            // about what is complete (a turn may have queried only some regions).
            // Deterministic wins ties, so behaviour stays stable run to run.
            ChartSpec? modelRival = charts.FirstOrDefault(c => c is not null
                && string.Equals(c.Type, deterministic.Type, StringComparison.OrdinalIgnoreCase));

            ChartSpec? richerModelChart = null;
            if (modelRival is not null
                && ChartSpecValidator.TryGetRenderable(modelRival, minSeries: 1, minMarks: requiredMarks, out ChartSpec? cleanedRival)
                && cleanedRival is not null
                && CountMarks(cleanedRival) > CountMarks(deterministic))
            {
                richerModelChart = cleanedRival;
            }

            ChartSpec chosen = richerModelChart ?? deterministic;
            _logger.LogInformation(
                "Chart-fulfillment: emitting a {ChartType} chart with {Marks} marks from the "
                + "{Source} source for an explicit chart request (deterministic-first, issue #172).",
                chosen.Type, CountMarks(chosen), richerModelChart is null ? "deterministic" : "model (richer)");

            charts.RemoveAll(c => c is null
                || string.Equals(c.Type, deterministic.Type, StringComparison.OrdinalIgnoreCase));
            charts.Insert(0, chosen);
            return new ChartFulfillmentResult(charts, StripFallbackClaims(reply));
        }

        // Already fulfilled by the model / inline recovery.
        if (charts.Count > 0)
        {
            // No deterministic reconstruction was possible, so the model-emitted
            // chart is the fallback — but it is held to the same renderability and
            // mark floor rather than trusted on sight. An under-populated chart
            // (e.g. a "compare all spirits brands" bar carrying one mark) is worse
            // than no chart: it renders, so it looks like success while silently
            // misrepresenting the data.
            ChartSpec? candidate = charts.FirstOrDefault(c => c is not null
                && (string.IsNullOrWhiteSpace(intent.ChartType)
                    || string.Equals(c.Type, intent.ChartType, StringComparison.OrdinalIgnoreCase)));

            if (candidate is not null
                && ChartSpecValidator.TryGetRenderable(candidate, minSeries: 1, minMarks: requiredMarks, out ChartSpec? cleanedModelChart)
                && cleanedModelChart is not null)
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

                int replaceAt = charts.IndexOf(candidate);
                charts[replaceAt] = cleanedModelChart;
                return new ChartFulfillmentResult(charts, sanitizedReply);
            }

            _logger.LogWarning(
                "Chart-fulfillment: dropping a model-emitted chart that does not meet the "
                + "{MinMarks}-mark floor for '{ChartType}' and could not be rebuilt from tool "
                + "results — failing closed rather than rendering an under-populated chart.",
                requiredMarks, intent.ChartType ?? "chart");
            charts.Clear();
        }

        // No renderable chart and no data to build one — the deterministic-first
        // attempt above already tried to reconstruct from this turn's tool results
        // with the same mark floor, so reaching here means the tool payload simply
        // does not contain a chartable shape for what was asked. Surface a precise,
        // structured diagnostic instead of a silent prose-only reply.
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

    /// <summary>Total finite datapoints across every series in a chart.</summary>
    private static int CountMarks(ChartSpec? chart)
    {
        if (chart is null) return 0;
        int n = 0;
        foreach (ChartSeries s in chart.Data)
        {
            foreach (ChartDataPoint p in s.Values)
            {
                if (p is not null && double.IsFinite(p.Y)) n++;
            }
        }
        return n;
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
    /// Result of <see cref="EnforceRequestedChartType"/>: the (possibly coerced) chart list,
    /// plus — when the model emitted a chart from a different structural family than the
    /// user asked for — the mismatch tuple so the caller can fail closed with a diagnostic.
    /// </summary>
    internal readonly record struct ChartTypeEnforcementResult(
        List<ChartSpec> Charts,
        (string ModelType, string RequestedType)? CrossFamilyMismatch);

    /// <summary>
    /// Family-aware enforcement of the chart type the user explicitly requested. Iterates
    /// every model-emitted chart:
    /// <list type="bullet">
    ///   <item>exact-type match → keep unchanged;</item>
    ///   <item>same family (identical data shape, presentation-only difference) → coerce
    ///     <see cref="ChartSpec.Type"/> to the requested type in place;</item>
    ///   <item>cross family → drop the chart and record the first mismatch so the caller
    ///     can fail closed with a chart-type-mismatch diagnostic. A silent rewrite here
    ///     would bind the wrong data shape and hide a real model error (issue #76 Group D).</item>
    /// </list>
    /// </summary>
    internal ChartTypeEnforcementResult EnforceRequestedChartType(
        List<ChartSpec> charts,
        string requestedType)
    {
        string[]? requestedFamily = ChartTypeFamily(requestedType);
        if (requestedFamily is null)
        {
            // The requested type isn't in our nine canonical types — no enforcement possible.
            return new ChartTypeEnforcementResult(charts, null);
        }

        (string ModelType, string RequestedType)? firstMismatch = null;
        var kept = new List<ChartSpec>(charts.Count);
        foreach (ChartSpec? chart in charts)
        {
            if (chart is null) continue;

            if (string.Equals(chart.Type, requestedType, StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(chart);
                continue;
            }

            string[]? actualFamily = ChartTypeFamily(chart.Type);
            if (actualFamily is not null && ReferenceEquals(actualFamily, requestedFamily))
            {
                // Within-family coercion: safe presentation-only rewrite.
                _logger.LogInformation(
                    "Chart-fulfillment: coercing model-emitted '{ModelType}' chart to user-stated "
                    + "'{RequestedType}' (same structural family) — issue #76 Group D.",
                    chart.Type, requestedType);
                kept.Add(chart with { Type = requestedType });
                continue;
            }

            // Cross-family (or unknown-family) mismatch: fail closed.
            firstMismatch ??= (chart.Type ?? "unknown", requestedType);
            _logger.LogWarning(
                "Chart-fulfillment: dropping cross-family chart — model emitted '{ModelType}', "
                + "user asked for '{RequestedType}' — issue #76 Group D.",
                chart.Type, requestedType);
        }

        return new ChartTypeEnforcementResult(kept, firstMismatch);
    }

    private static string BuildChartTypeMismatchDiagnostic(string modelType, string requestedType)
    {
        return $"⚠️ Chart unavailable: you asked for a {requestedType} chart, but the underlying "
            + $"tools returned data shaped for a {modelType} chart — a different chart family. "
            + "The data shape and the requested chart type are incompatible, so no chart is emitted. "
            + $"Please retry and confirm the request is for a {modelType}-shaped visualization, or "
            + $"narrow the query so a {requestedType}-shaped answer can be produced.";
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

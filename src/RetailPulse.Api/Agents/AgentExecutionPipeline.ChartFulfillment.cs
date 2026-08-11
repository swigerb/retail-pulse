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

        // Not an explicit chart request → never force a chart, and drop any chart the
        // model produced anyway. Group A (#76): production sweep showed the LLM emitting
        // ChartSpecs on prose prompts that contain trigger nouns ("Compare", "Show me
        // ... trends") but NO explicit chart noun. The ChartRequestDetector unit test
        // classifies these as prose correctly, but the fulfillment path only ever
        // enforced "chart must exist when requested" — it never enforced the inverse
        // "chart must NOT exist when not requested". The specialist has CreateChart
        // wired into its toolkit for the legitimate chart prompts, and nothing stopped
        // the model from calling it on a prose prompt. That is the exact test/production
        // divergence for the #76 Group A regression (recurrence of the #50 bug class).
        //
        // Enforcing this inverse invariant here is deterministic (same prompt → same
        // decision, regardless of model non-determinism) and tenant-generic (driven
        // only by the detector, no prompt/brand literals).
        if (!intent.IsExplicitChartRequest)
        {
            if (charts.Count > 0)
            {
                _logger.LogWarning(
                    "Chart-fulfillment: dropping {Count} model-emitted chart(s) on a non-chart prompt — "
                    + "the detector classifies this as prose (no explicit chart noun), so a chart on the "
                    + "response would be an unrequested visualization (issue #76 Group A).",
                    charts.Count);
                charts.Clear();
            }
            return new ChartFulfillmentResult(charts, reply);
        }

        // Explicit chart request with a user-stated type. Group D (#76): user asked
        // for a "bar chart" but the model emitted a horizontalBar. The user's stated
        // type must win over model/heuristic drift. We coerce the Type field on any
        // chart whose declared type is in the SAME structural family as the user's
        // request (bar shapes / line shapes / pie shapes) — those share a data shape
        // so the coercion is a pure rendering-orientation fix. Cross-family mismatches
        // are left alone (data would not bind). Deterministic: same input → same
        // coercion, and a no-op when types already match.
        if (!string.IsNullOrWhiteSpace(intent.ChartType))
        {
            CoerceChartTypesToUserRequest(charts, intent.ChartType);
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

    // Structural chart-type families: within a family the ChartSpec data shape is
    // interchangeable, so coercing between family members is a rendering-orientation
    // fix (Group D). Cross-family coercion is unsafe because the data would not bind.
    private static readonly string[][] _chartTypeFamilies =
    [
        ["bar", "horizontalBar", "column", "stackedBar", "groupedBar"],
        ["line", "area"],
        ["pie", "donut"],
    ];

    private static string[]? FamilyFor(string type)
    {
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

    /// <summary>
    /// Coerce every chart whose declared type is in the same structural family as the
    /// user-stated type to that user-stated type. Preserves the chart data verbatim —
    /// only the <c>Type</c> rendering hint changes. No-op when types already match or
    /// when the mismatch crosses families (unsafe: data shape would not bind). This is
    /// the deterministic fix for the #76 Group D failure ("Show me a bar chart …" →
    /// production returned horizontalBar).
    /// </summary>
    internal void CoerceChartTypesToUserRequest(List<ChartSpec> charts, string requestedType)
    {
        string[]? requestedFamily = FamilyFor(requestedType);
        if (requestedFamily is null) return;

        for (int i = 0; i < charts.Count; i++)
        {
            ChartSpec chart = charts[i];
            if (chart is null) continue;
            if (string.Equals(chart.Type, requestedType, StringComparison.OrdinalIgnoreCase))
                continue;

            string[]? actualFamily = FamilyFor(chart.Type);
            if (actualFamily is null || !ReferenceEquals(actualFamily, requestedFamily))
                continue; // Cross-family or unknown: leave alone.

            _logger.LogInformation(
                "Chart-fulfillment: coercing model-emitted '{ModelType}' chart to user-stated "
                + "'{RequestedType}' (same structural family) — issue #76 Group D.",
                chart.Type, requestedType);
            charts[i] = chart with { Type = requestedType };
        }
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

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
            // Even on a prose reply the model sometimes echoes a raw ```json { ... } ```
            // chart-spec block inside the answer text (Publix sweep #76 spot-14 prose ask
            // returned a fenced bar-chart JSON blob). Scrub any fenced JSON so the user
            // sees prose only, matching the detector's decision.
            return new ChartFulfillmentResult(charts, StripJsonCodeFences(reply));
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

        // Roster-coverage invariant. When the user asked for a chart covering a
        // KNOWN SUBSET of the tenant roster — either every brand (portfolio ranking)
        // or every brand in a specific tenant category ("all X brands …") — the
        // answer MUST cover every brand in that subset. A model-emitted chart that
        // silently drops half the group is treated as non-fulfilling and replaced
        // with the deterministic reconstruction from the tool payload, which the
        // compactor preserves in full. Tenant-generic: the roster is driven entirely
        // by tenant.yaml (no brand or count literals) so this also catches Publix
        // sweep #25 ("all home improvement brands by region") — the same failure
        // class as #74 but for a category-scoped subset and the table chart type.
        (IReadOnlyCollection<string> Brands, string Scope)? coverage =
            ResolveCoverageRoster(userMessage, intent, isPortfolioRanking);
        IReadOnlyCollection<string>? roster = coverage?.Brands;
        // Chart types that participate in the coverage invariant. Kept in sync with
        // DeterministicChartBuilder — every builder here must accept the requiredBrands
        // contract in TryBuild.
        static bool ChartTypeParticipatesInCoverage(string? type) =>
            string.Equals(type, "horizontalBar", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "table", StringComparison.OrdinalIgnoreCase);

        if (roster is { Count: > 0 } && ChartTypeParticipatesInCoverage(intent.ChartType))
        {
            bool alreadyCovers = charts.Any(c =>
                ChartTypeParticipatesInCoverage(c?.Type)
                && DeterministicChartBuilder.CoversRoster(c, roster));

            if (!alreadyCovers)
            {
                int minMarks = Math.Max(
                    ChartSpecValidator.MinimumMarksForType(intent.ChartType),
                    roster.Count);
                if (DeterministicChartBuilder.TryBuild(response, intent.ChartType, minMarks, roster, out ChartSpec? rebuilt)
                    && rebuilt is not null)
                {
                    _logger.LogInformation(
                        "Chart-fulfillment: replacing model-emitted {ChartType} with deterministic "
                        + "chart covering all {BrandCount} required tenant brand(s) for scope '{Scope}'.",
                        intent.ChartType, roster.Count, coverage!.Value.Scope);
                    // Drop any prior chart(s) of a coverage-participating type — the
                    // deterministic roster-complete chart is the source of truth for
                    // this intent.
                    charts.RemoveAll(c => ChartTypeParticipatesInCoverage(c?.Type));
                    charts.Add(rebuilt);
                    return new ChartFulfillmentResult(charts, StripJsonCodeFences(StripFallbackClaims(reply)));
                }

                // Coverage impossible from the current tool payload — fail closed with a
                // diagnostic listing exactly which brands are missing, and drop any
                // partial model chart so the user is not silently misled.
                IReadOnlyList<string> missing = ComputeMissingBrands(charts, roster);
                _logger.LogWarning(
                    "Chart-fulfillment: {Scope} coverage missing {Missing} brand(s) — failing closed.",
                    coverage!.Value.Scope, missing.Count);
                // When we are failing closed on a coverage-scoped chart request, drop
                // EVERY model-emitted chart — not just the coverage-participating types
                // (issue #76 Publix sweep: a groupedBar chart the model emitted alongside
                // an unfulfillable table request survived the previous narrower filter
                // and gave the user a rogue chart under a "chart unavailable" prose
                // header). If the requested chart cannot be produced with full roster
                // coverage, NO chart is a truthful outcome; a partial chart is not.
                charts.Clear();
                string diag = BuildRankingCoverageDiagnostic(missing, roster.Count);
                // Scrub the model's fallback/truncation narrative from the prose so
                // the user-visible reply cannot claim a chart was produced when we
                // are in fact failing closed (issue #74 P0 failure #2). Also strip
                // any raw JSON code fence the model may have inlined in prose
                // (issue #76 Publix sweep: model emitted a ```json { "chart": ... }```
                // block alongside the refusal, leaking schema to the user).
                string scrubbed = StripFallbackClaims(reply);
                scrubbed = StripJsonCodeFences(scrubbed);
                string updated = string.IsNullOrWhiteSpace(scrubbed) ? diag : $"{scrubbed}\n\n{diag}";
                return new ChartFulfillmentResult(charts, updated);
            }
        }

        // ── Deterministic auto-emit for table / pie / donut chart intents ──
        //
        // The generalization of the #74 architecture (deterministic reconstruction
        // when the model chart is absent or non-covering) to the table & pie/donut
        // families (issue #76 acceptance gaps #21 and #25). For these intents the
        // required data shape is unambiguous and comes from a single aggregate
        // (GetPortfolioDepletionStats.brands[] or national_share.entries[]) that
        // the ToolPrefetchService already pre-calls into context. Chart emission
        // must be a function of (routed intent × chart type × available data) —
        // NOT of whether the specialist happened to call CreateChart on this run.
        //
        // Behavior when the user asked for a table / pie / donut:
        //   * If the model emitted a same-family chart already, keep it (below).
        //   * Otherwise, attempt deterministic reconstruction from tool payloads.
        //     If it succeeds we ADD the deterministic chart of the correct family
        //     and DROP any cross-family model chart (an un-requested visualization,
        //     the same failure class as #76 Group A). If it fails we fall through
        //     to the normal fulfilment path (fail-closed diagnostic below).
        //
        // Deliberately scoped to table / pie / donut so it cannot regress the
        // existing bar/line safety invariants (test EnforceChartFulfillment_
        // CrossFamilyMismatch_LeavesTypeAlone). Bar/line intents keep the model
        // chart when present — their data shapes are less unambiguous to rebuild
        // and #74's roster path already handles the bar/horizontalBar coverage
        // case explicitly.
        static bool IsDeterministicAutoEmitFamily(string? type) =>
            string.Equals(type, "table", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "pie", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "donut", StringComparison.OrdinalIgnoreCase);

        if (IsDeterministicAutoEmitFamily(intent.ChartType))
        {
            bool alreadyHasCorrectFamilyChart = charts.Any(c => SameFamily(c?.Type, intent.ChartType));
            if (!alreadyHasCorrectFamilyChart)
            {
                int autoMinMarks = ChartSpecValidator.MinimumMarksForType(intent.ChartType);
                // Prefer coverage-checked reconstruction when a roster is known
                // (category-scoped table request). Otherwise unrestricted build.
                bool built = roster is { Count: > 0 } && ChartTypeParticipatesInCoverage(intent.ChartType)
                    ? DeterministicChartBuilder.TryBuild(response, intent.ChartType, Math.Max(autoMinMarks, roster.Count), roster, out ChartSpec? autoChart)
                    : DeterministicChartBuilder.TryBuild(response, intent.ChartType, autoMinMarks, out autoChart);

                if (built && autoChart is not null)
                {
                    int droppedCrossFamily = charts.RemoveAll(c => !SameFamily(c?.Type, intent.ChartType));
                    _logger.LogInformation(
                        "Chart-fulfillment: deterministic auto-emit of {ChartType} chart from tool payloads "
                        + "for an explicit {IntentType} request (dropped {Dropped} cross-family model chart(s)) — "
                        + "chart emission is now a function of intent × data, not model choice (issue #76).",
                        autoChart.Type, intent.ChartType, droppedCrossFamily);
                    charts.Add(autoChart);
                    return new ChartFulfillmentResult(charts, StripJsonCodeFences(StripFallbackClaims(reply)));
                }
            }
        }

        // Already fulfilled by the model / inline recovery.
        if (charts.Count > 0)
        {
            // If a roster-complete coverage-scoped chart is present, scrub any
            // fallback/truncation vocabulary the model may have narrated into the
            // prose (issue #74) — the chart is authoritative and the prose must
            // not undermine it.
            string sanitizedReply = (roster is { Count: > 0 } && charts.Any(c =>
                    ChartTypeParticipatesInCoverage(c?.Type)
                    && DeterministicChartBuilder.CoversRoster(c, roster)))
                ? StripFallbackClaims(reply)
                : reply;
            // Whenever a chart is present, scrub any fenced JSON blob the model
            // echoed alongside the answer — the chart is the authoritative binding
            // and a raw ```json {"chart":...}``` next to it leaks internal schema
            // (issue #76 Publix sweep det-19-r1 line ask leaked a spec fence).
            sanitizedReply = StripJsonCodeFences(sanitizedReply);
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

        if (DeterministicChartBuilder.TryBuild(response, intent.ChartType, fallbackMinMarks, out ChartSpec? built2) && built2 is not null)
        {
            _logger.LogInformation(
                "Chart-fulfillment: reconstructed a {ChartType} chart deterministically from tool results "
                + "for an explicit chart request that returned prose-only.",
                built2.Type);
            charts.Add(built2);
            return new ChartFulfillmentResult(charts, StripJsonCodeFences(reply));
        }

        // No renderable chart and no data to build one — surface a precise, structured
        // diagnostic instead of a silent prose-only reply.
        _logger.LogWarning(
            "Chart-fulfillment: explicit {ChartType} chart request could not be satisfied — "
            + "no renderable chart and no chartable tool data present; emitting chart-unavailable diagnostic.",
            intent.ChartType ?? "chart");

        string diagnostic = BuildChartUnavailableDiagnostic(intent.ChartType);
        string scrubbedReplyForDiag = StripJsonCodeFences(reply);
        string updatedReply = string.IsNullOrWhiteSpace(scrubbedReplyForDiag)
            ? diagnostic
            : $"{scrubbedReplyForDiag}\n\n{diagnostic}";

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
    /// True when <paramref name="a"/> and <paramref name="b"/> belong to the same
    /// structural chart-type family (see <see cref="_chartTypeFamilies"/>). Tables
    /// are treated as their own family — a table intent is only satisfied by a
    /// table chart. Used by the deterministic auto-emit path to decide whether the
    /// model-emitted chart already fulfils the user's stated chart family.
    /// </summary>
    private static bool SameFamily(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        // Table has no family peers — an exact-type match is required.
        if (string.Equals(a, "table", StringComparison.OrdinalIgnoreCase)
            || string.Equals(b, "table", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string[]? fa = FamilyFor(a);
        string[]? fb = FamilyFor(b);
        return fa is not null && fb is not null && ReferenceEquals(fa, fb);
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
            if (!string.Equals(chart.Type, "horizontalBar", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(chart.Type, "table", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (ChartSeries s in chart.Data)
            {
                foreach (ChartDataPoint p in s.Values)
                {
                    if (p?.X is null || !double.IsFinite(p.Y)) continue;
                    // Table labels take the shape "Brand — Region"; coverage is on
                    // the brand token only (tenant-generic — no brand or region literals).
                    present.Add(ExtractBrandToken(p.X));
                }
            }
        }
        return [.. roster.Where(b => !present.Contains(b))];
    }

    /// <summary>
    /// Splits a chart label of the form "Brand — Region" into its brand token, or
    /// returns the label unchanged when no em-dash separator is present. Used by
    /// the coverage invariant so a table row labelled "Pinnacle Hardware — Southeast"
    /// counts toward the brand roster (Publix sweep #25).
    /// </summary>
    internal static string ExtractBrandToken(string label)
    {
        if (string.IsNullOrEmpty(label)) return label;
        int sep = label.IndexOf(" — ", StringComparison.Ordinal);
        return sep > 0 ? label[..sep].Trim() : label.Trim();
    }

    private static string BuildRankingCoverageDiagnostic(IReadOnlyList<string> missing, int rosterCount)
    {
        string list = missing.Count == 0
            ? "one or more required brands"
            : string.Join(", ", missing);
        return $"⚠️ Chart unavailable: this request must cover every required brand "
            + $"({rosterCount} total for this scope), but the following were not returned by the "
            + $"underlying data tools: {list}. This is a data-availability issue, not a rendering "
            + "failure — a partial answer would silently mis-represent the scope, so no chart is emitted.";
    }

    /// <summary>
    /// Resolves the brand roster that a chart response MUST cover, or <c>null</c>
    /// when the request does not have a bounded brand scope. Two shapes are handled:
    /// <list type="bullet">
    ///   <item>portfolio ranking (existing #74 invariant) — every tenant brand;</item>
    ///   <item>category-scoped requests such as "all home improvement brands …"
    ///     (Publix sweep #25) — every brand in the matched tenant category.</item>
    /// </list>
    /// Tenant-generic: category matching walks <c>tenant.yaml</c>'s configured
    /// categories, no prompt or brand literals. When multiple categories match
    /// the message the smallest (most specific) match wins so "all X and Y brands"
    /// does not silently collapse into an unrelated category.
    /// </summary>
    private (IReadOnlyCollection<string> Brands, string Scope)? ResolveCoverageRoster(
        string? userMessage,
        ChartIntent intent,
        bool isPortfolioRanking)
    {
        if (_tenant.Brands.Count == 0) return null;

        if (isPortfolioRanking)
        {
            return (_tenant.Brands.Select(b => b.Name).ToArray(), "portfolio");
        }

        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return null;
        }

        // Category-scoped "all X brands" pattern: only fires when the message
        // contains both an "all …/every … brands" quantifier and the name of a
        // configured tenant category.
        if (!HasCategoryQuantifier(userMessage))
        {
            return null;
        }

        BrandConfig[] matched = FindCategoryScopedBrands(userMessage);
        if (matched.Length < 2)
        {
            return null;
        }

        string category = matched[0].Category;
        return (matched.Select(b => b.Name).ToArray(), $"category:{category}");
    }

    private static bool HasCategoryQuantifier(string message)
    {
        string[] cues =
        [
            "all ",
            "every ",
            "each ",
        ];
        foreach (string cue in cues)
        {
            int idx = message.IndexOf(cue, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                // Require "brand" or "brands" (or "category") in the tail so we
                // don't misfire on "all regions", "all quarters", etc.
                int tailStart = idx + cue.Length;
                int windowEnd = Math.Min(message.Length, tailStart + 80);
                string tail = message[tailStart..windowEnd];
                if (tail.Contains("brand", StringComparison.OrdinalIgnoreCase)
                    || tail.Contains("categor", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                idx = message.IndexOf(cue, idx + 1, StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }

    private BrandConfig[] FindCategoryScopedBrands(string message)
    {
        // Rank matching categories by descending name length so the most specific
        // wins ("Quick-Serve Restaurant" beats "Restaurant") — tenant-generic.
        IEnumerable<string> categories = _tenant.Brands
            .Select(b => b.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(c => c.Length);

        foreach (string category in categories)
        {
            if (message.Contains(category, StringComparison.OrdinalIgnoreCase))
            {
                return [.. _tenant.Brands.Where(b => string.Equals(b.Category, category, StringComparison.OrdinalIgnoreCase))];
            }
        }
        return [];
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

    // Matches a fenced code block whose info string is empty or 'json' (case-
    // insensitive). Multi-line, non-greedy body. Used by the fail-closed path so a
    // model that leaks a raw chart-spec JSON blob alongside a chart-unavailable
    // refusal cannot surface schema fragments to the end user (issue #76 sweep #25).
    [System.Text.RegularExpressions.GeneratedRegex(@"```(?:json)?\s*[\r\n][\s\S]*?```",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex JsonCodeFencePattern();

    /// <summary>
    /// Removes any ``` ... ``` or ```json ... ``` fenced code blocks from
    /// <paramref name="reply"/>. Only touches complete fences; unfenced JSON is
    /// left alone (the inline-chart extractor handles those). Called from the
    /// coverage fail-closed path so a leaked schema fragment never reaches the
    /// user under a refusal header.
    /// </summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"(\r?\n){3,}")]
    private static partial System.Text.RegularExpressions.Regex ExcessiveBlankLinesPatternForFenceStrip();

    internal static string StripJsonCodeFences(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return reply ?? string.Empty;
        string stripped = JsonCodeFencePattern().Replace(reply, string.Empty);
        // Collapse the extra blank lines the fence removal leaves behind.
        stripped = ExcessiveBlankLinesPatternForFenceStrip().Replace(stripped, "\n\n").Trim();
        return stripped;
    }
}

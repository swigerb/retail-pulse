using System.Text.Json;
using Microsoft.Extensions.AI;
using RetailPulse.Contracts;

namespace RetailPulse.Api.Charts;

/// <summary>
/// Builds a renderable <see cref="ChartSpec"/> deterministically from the tool results
/// already captured in an agent response — no second LLM call. Used to satisfy the
/// chart-fulfillment invariant: when the user explicitly asked for a chart but the model
/// returned prose only (a common failure after tool-result compaction, where the model
/// wrongly treats a complete aggregate as unusable), the pipeline reconstructs the chart
/// from the same aggregate figures the model already had.
///
/// It is shape-driven (not name-driven) so it works regardless of provider tool-call
/// metadata, and it never fabricates values — a chart is produced only when the captured
/// payloads carry the required finite numbers.
/// </summary>
internal static class DeterministicChartBuilder
{
    private static readonly JsonDocumentOptions _docOptions = new() { AllowTrailingCommas = true };

    /// <summary>
    /// Attempt to build a chart of the requested kind from the response's tool results.
    /// </summary>
    /// <param name="response">The completed chat response whose messages hold FunctionResultContent.</param>
    /// <param name="requestedType">
    /// The chart type the user asked for (from <see cref="ChartRequestDetector"/>), or null.
    /// A "gauge" request builds a gauge from inventory/supply-health data; anything else
    /// builds a bar comparison from historical-demand data. If the primary shape is absent
    /// the builder falls back to the other before giving up.
    /// </param>
    public static bool TryBuild(
        Microsoft.Extensions.AI.ChatResponse response,
        string? requestedType,
        out ChartSpec? chart)
        => TryBuild(response, requestedType, minMarks: 0, out chart);

    /// <summary>
    /// Attempt to build a chart of the requested kind, subject to a minimum-finite-marks
    /// contract. When <paramref name="minMarks"/> is greater than zero the result is
    /// rejected unless the cleaned chart carries at least that many finite datapoints;
    /// for horizontal-bar ranking requests the builder additionally requires at least
    /// one non-zero mark and refuses to fall through to a velocity chart when only
    /// historical-demand payloads (i.e. no <c>brands[]</c> growth aggregate) are
    /// present. This is the fail-closed contract that stops a growth-ranking prompt
    /// from surfacing a zero-valued or velocity-relabelled chart shell.
    /// </summary>
    public static bool TryBuild(
        Microsoft.Extensions.AI.ChatResponse response,
        string? requestedType,
        int minMarks,
        out ChartSpec? chart)
        => TryBuild(response, requestedType, minMarks, requiredBrands: null, out chart);

    /// <summary>
    /// Overload adding a portfolio-coverage contract for horizontal-bar ranking requests:
    /// when <paramref name="requiredBrands"/> is non-empty AND the requested type is
    /// <c>horizontalBar</c>, the produced ranking chart MUST contain a mark for every
    /// required brand (case-insensitive). This is the tenant-generic guard that stops a
    /// portfolio ranking from silently dropping half the portfolio because the model
    /// emitted only the brands it happened to prioritize — the source of truth is the
    /// aggregate tool payload, and coverage is enforced against the tenant roster.
    /// </summary>
    public static bool TryBuild(
        Microsoft.Extensions.AI.ChatResponse response,
        string? requestedType,
        int minMarks,
        IReadOnlyCollection<string>? requiredBrands,
        out ChartSpec? chart)
    {
        chart = null;
        List<JsonElement> payloads = CollectToolPayloads(response);
        if (payloads.Count == 0)
        {
            return false;
        }

        bool gaugeFirst = string.Equals(requestedType, "gauge", StringComparison.OrdinalIgnoreCase);
        bool isHorizontalRanking = string.Equals(requestedType, "horizontalBar", StringComparison.OrdinalIgnoreCase);

        ChartSpec? built = SelectBuilder(payloads, requestedType, gaugeFirst, isHorizontalRanking);

        if (built is null)
        {
            return false;
        }

        int effectiveMinMarks = Math.Max(minMarks, 1);
        if (!ChartSpecValidator.TryGetRenderable(built, minSeries: 1, minMarks: effectiveMinMarks, out ChartSpec? renderable)
            || renderable is null)
        {
            return false;
        }

        // Ranking contract: a horizontalBar growth ranking must not be all zeros.
        // A chart of exclusively-zero marks passes chartIsRenderable but paints as
        // an empty shell with no visible bars — the exact P0 failure. Require at
        // least one non-zero finite mark whenever a horizontal ranking is asked for.
        if (isHorizontalRanking && !ChartSpecValidator.HasNonZeroFinitePoint(renderable))
        {
            return false;
        }

        // Portfolio coverage contract: when the caller supplies the tenant roster and the
        // request is a horizontal-bar ranking ("rank ALL brands …"), the built chart MUST
        // cover every tenant brand. If any is missing, fail closed so the pipeline can
        // surface the chart-unavailable diagnostic listing exactly which brands were
        // omitted — never silently return a partial portfolio.
        if (isHorizontalRanking && requiredBrands is { Count: > 0 })
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ChartSeries s in renderable.Data)
            {
                foreach (ChartDataPoint p in s.Values)
                {
                    if (p?.X is not null)
                        present.Add(p.X);
                }
            }
            foreach (string brand in requiredBrands)
            {
                if (!present.Contains(brand))
                    return false;
            }
        }

        chart = renderable;
        return true;
    }

    /// <summary>
    /// True when the given chart is a horizontal-bar ranking that covers every brand in
    /// <paramref name="requiredBrands"/> (case-insensitive) and has at least one non-zero
    /// finite mark. Used by the fulfillment invariant to decide whether a model-emitted
    /// chart already satisfies the portfolio-coverage contract or must be replaced with
    /// the deterministic reconstruction from tool results.
    /// </summary>
    public static bool CoversRoster(ChartSpec? chart, IReadOnlyCollection<string> requiredBrands)
    {
        if (chart is null || requiredBrands.Count == 0)
            return false;
        if (!ChartSpecValidator.HasNonZeroFinitePoint(chart))
            return false;

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartSeries s in chart.Data)
        {
            foreach (ChartDataPoint p in s.Values)
            {
                if (p?.X is not null && double.IsFinite(p.Y))
                    present.Add(p.X);
            }
        }
        foreach (string brand in requiredBrands)
        {
            if (!present.Contains(brand))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Chooses the deterministic builder that matches the requested chart type, falling
    /// back through the other shape-driven builders when the primary data shape is absent.
    /// Order is type-led so a horizontal-bar ranking request never collapses into a plain
    /// velocity bar while portfolio growth data is present, and a grouped/region request
    /// prefers the two-brand region rollup.
    /// </summary>
    private static ChartSpec? SelectBuilder(
        IReadOnlyList<JsonElement> payloads,
        string? requestedType,
        bool gaugeFirst,
        bool isHorizontalRanking = false)
    {
        _ = isHorizontalRanking; // reserved: caller signals the ranking contract via the outer TryBuild
        return gaugeFirst
            ? TryBuildGauge(payloads) ?? TryBuildDemandBar(payloads, requestedType)
            : (requestedType?.ToLowerInvariant()) switch
            {
                // Horizontal-bar RANKING has a single legitimate shape: a portfolio growth
                // ranking built from GetPortfolioDepletionStats' brands[] payload. If that
                // is not present we FAIL CLOSED (return null) rather than silently rebrand
                // a velocity bar as growth or emit a groupedBar under a horizontalBar
                // request — the P0 failure this fix addresses. The caller enforces the
                // fail-closed contract via the chart-unavailable diagnostic.
                "horizontalbar" => TryBuildGrowthRanking(payloads),
                "groupedbar" or "stackedbar" => TryBuildGroupedRegionBar(payloads, requestedType)
                    ?? TryBuildDemandBar(payloads, requestedType),
                "line" => TryBuildDemandLine(payloads)
                    ?? TryBuildDemandBar(payloads, "line"),
                // Pie/donut and table are their own structural families (share/mix
                // proportions; a row grid). Falling through to a bar here produced a
                // chart in a DIFFERENT family from the one the user asked for — the
                // pipeline's own Group D guard would then have to drop it, so the
                // fallback could only ever waste work or, when type enforcement was
                // bypassed, leak a bar under a "create a table…" request. Fail closed
                // instead, exactly as the horizontalBar ranking above does.
                "pie" or "donut" => TryBuildShareOrMixPie(payloads, requestedType ?? "pie"),
                "table" => TryBuildDepletionStatsTable(payloads),
                _ => TryBuildDemandBar(payloads, requestedType) ?? TryBuildGauge(payloads),
            };
    }

    private static List<JsonElement> CollectToolPayloads(Microsoft.Extensions.AI.ChatResponse response)
    {
        var payloads = new List<JsonElement>();
        foreach (ChatMessage msg in response.Messages)
        {
            foreach (AIContent content in msg.Contents)
            {
                if (content is not FunctionResultContent toolResult)
                    continue;

                string? text = toolResult.Result?.ToString();
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(text, _docOptions);
                    payloads.Add(doc.RootElement.Clone());
                }
                catch (JsonException)
                {
                    // Non-JSON tool output — ignore for deterministic charting.
                }
            }
        }
        return payloads;
    }

    /// <summary>
    /// Build a bar chart comparing average depletion velocity (avg weekly volume) across
    /// every distinct GetHistoricalDemand payload (one per brand). Works on both the raw
    /// and the compacted ("aggregate_complete") payload shapes.
    /// </summary>
    private static ChartSpec? TryBuildDemandBar(IReadOnlyList<JsonElement> payloads, string? requestedType)
    {
        var points = new List<ChartDataPoint>();
        var seenBrands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? region = null;

        foreach (JsonElement payload in payloads)
        {
            if (payload.ValueKind != JsonValueKind.Object)
                continue;
            if (!payload.TryGetProperty("summary", out JsonElement summary) ||
                summary.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            // Historical-demand fingerprint: a summary with a total_volume, plus either
            // weekly_data (raw) or by_region (compacted).
            bool isDemand = summary.TryGetProperty("total_volume", out _)
                && (payload.TryGetProperty("weekly_data", out _) || payload.TryGetProperty("by_region", out _));
            if (!isDemand)
                continue;

            string brand = ReadBrand(payload) ?? $"Series {points.Count + 1}";
            region ??= ReadRegion(payload);

            if (!TryReadDouble(summary, "avg_weekly_volume", out double velocity))
            {
                // Derive velocity if the average wasn't projected.
                if (TryReadDouble(summary, "total_volume", out double totalVol)
                    && TryReadDouble(summary, "weeks_of_data", out double weeks) && weeks > 0)
                {
                    velocity = Math.Round(totalVol / weeks, 1);
                }
                else
                {
                    continue;
                }
            }

            if (!double.IsFinite(velocity) || !seenBrands.Add(brand))
                continue;

            points.Add(new ChartDataPoint { X = brand, Y = velocity });
        }

        if (points.Count == 0)
            return null;

        string type = NormalizeBarType(requestedType);
        string title = region is null
            ? "Depletion Velocity by Brand"
            : $"Depletion Velocity by Brand — {region}";

        return new ChartSpec
        {
            Type = type,
            Title = title,
            XAxisTitle = "Brand",
            YAxisTitle = "Avg Weekly Depletion Velocity",
            Data =
            [
                new ChartSeries { Legend = "Avg Weekly Depletion Velocity", Values = points }
            ]
        };
    }

    /// <summary>
    /// Build a grouped (or stacked) bar of depletion volume by region for every distinct
    /// brand demand payload. Reads the compacted <c>by_region</c> rollup (region → volume)
    /// from each brand's GetHistoricalDemand result and emits one series per brand with a
    /// shared region category axis. This is the deterministic fix for the empty grouped-bar
    /// P0: two brand series × the available regions, every mark a finite volume drawn from
    /// real data. Missing/unparseable region volumes are skipped — never coerced to zero —
    /// so a brand that genuinely has no data for a region simply omits that mark rather than
    /// planting a fake zero bar.
    /// </summary>
    private static ChartSpec? TryBuildGroupedRegionBar(IReadOnlyList<JsonElement> payloads, string? requestedType)
    {
        var series = new List<ChartSeries>();
        var seenBrands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement payload in payloads)
        {
            if (payload.ValueKind != JsonValueKind.Object)
                continue;
            if (!payload.TryGetProperty("by_region", out JsonElement byRegion) || byRegion.ValueKind != JsonValueKind.Array)
                continue;
            if (!payload.TryGetProperty("summary", out JsonElement summary) || summary.ValueKind != JsonValueKind.Object)
                continue;

            string brand = ReadBrand(payload) ?? $"Series {series.Count + 1}";
            if (!seenBrands.Add(brand))
                continue;

            var points = new List<ChartDataPoint>();
            foreach (JsonElement row in byRegion.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                    continue;
                if (!row.TryGetProperty("region", out JsonElement regionEl) || regionEl.ValueKind != JsonValueKind.String)
                    continue;
                // Prefer volume; never substitute zero for a missing metric.
                if (!TryReadDouble(row, "volume", out double volume) || !double.IsFinite(volume))
                    continue;
                points.Add(new ChartDataPoint { X = regionEl.GetString()!, Y = Math.Round(volume, 1) });
            }

            if (points.Count > 0)
                series.Add(new ChartSeries { Legend = brand, Values = points });
        }

        // A grouped/region comparison needs at least two brand series to be meaningful.
        if (series.Count < 2)
            return null;

        string type = string.Equals(requestedType, "stackedBar", StringComparison.OrdinalIgnoreCase)
            ? "stackedBar"
            : "groupedBar";
        string brands = string.Join(" vs ", series.Select(s => s.Legend));

        return new ChartSpec
        {
            Type = type,
            Title = $"{brands} — Depletion Volume by Region",
            XAxisTitle = "Region",
            YAxisTitle = "Depletion Volume",
            Data = series,
        };
    }

    /// <summary>
    /// Build a line of depletion volume across regions from a single brand's compacted
    /// <c>by_region</c> rollup ("depletion trends across all regions"). One series, one
    /// finite point per region. Missing region volumes are skipped, never zero-filled.
    /// </summary>
    private static ChartSpec? TryBuildDemandLine(IReadOnlyList<JsonElement> payloads)
    {
        foreach (JsonElement payload in payloads)
        {
            if (payload.ValueKind != JsonValueKind.Object)
                continue;
            if (!payload.TryGetProperty("by_region", out JsonElement byRegion) || byRegion.ValueKind != JsonValueKind.Array)
                continue;
            if (!payload.TryGetProperty("summary", out JsonElement summary) || summary.ValueKind != JsonValueKind.Object)
                continue;

            var points = new List<ChartDataPoint>();
            foreach (JsonElement row in byRegion.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                    continue;
                if (!row.TryGetProperty("region", out JsonElement regionEl) || regionEl.ValueKind != JsonValueKind.String)
                    continue;
                if (!TryReadDouble(row, "volume", out double volume) || !double.IsFinite(volume))
                    continue;
                points.Add(new ChartDataPoint { X = regionEl.GetString()!, Y = Math.Round(volume, 1) });
            }

            if (points.Count >= 2)
            {
                string brand = ReadBrand(payload) ?? "Depletion";
                return new ChartSpec
                {
                    Type = "line",
                    Title = $"{brand} Depletion Trend by Region",
                    XAxisTitle = "Region",
                    YAxisTitle = "Depletion Volume",
                    Data = [new ChartSeries { Legend = brand, Values = points }],
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Build a horizontal bar ranking every portfolio brand by depletion growth rate
    /// (YoY %), sorted descending. Reads the single compacted GetPortfolioDepletionStats
    /// payload (<c>brands[].depletions_yoy</c>) — one bounded response, no per-brand
    /// fan-out. This is the deterministic fix for the "ranking all brands by depletion
    /// growth rate" refusal. Brands whose growth value is missing/unparseable are excluded
    /// entirely — never charted as zero growth.
    /// </summary>
    private static ChartSpec? TryBuildGrowthRanking(IReadOnlyList<JsonElement> payloads)
    {
        foreach (JsonElement payload in payloads)
        {
            if (payload.ValueKind != JsonValueKind.Object)
                continue;
            if (!payload.TryGetProperty("brands", out JsonElement brands) || brands.ValueKind != JsonValueKind.Array)
                continue;

            var ranked = new List<(string Brand, double Growth)>();
            foreach (JsonElement brand in brands.EnumerateArray())
            {
                if (brand.ValueKind != JsonValueKind.Object)
                    continue;
                string? name = ReadNestedString(brand, "brand");
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                // Compacted portfolio flattens metrics onto the row; raw nests under "metrics".
                if (!TryReadPercent(brand, "depletions_yoy", out double growth)
                    && !(brand.TryGetProperty("metrics", out JsonElement metrics)
                        && metrics.ValueKind == JsonValueKind.Object
                        && TryReadPercent(metrics, "depletions_yoy", out growth)))
                {
                    continue; // missing growth → excluded, never zero-filled
                }

                if (double.IsFinite(growth))
                    ranked.Add((name, growth));
            }

            if (ranked.Count >= 2)
            {
                var points = ranked
                    .OrderByDescending(r => r.Growth)
                    .Select(r => new ChartDataPoint { X = r.Brand, Y = Math.Round(r.Growth, 1) })
                    .ToList();

                return new ChartSpec
                {
                    Type = "horizontalBar",
                    Title = "Brands Ranked by Depletion Growth Rate (YoY)",
                    XAxisTitle = "Depletion Growth Rate % (YoY)",
                    YAxisTitle = "Brand",
                    Data = [new ChartSeries { Legend = "Depletion Growth Rate % (YoY)", Values = points }],
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Build a pie/donut of market-share or variant-mix percentages from a single payload.
    /// Reads market share (<c>share_data[].share_percent</c>) or variant mix
    /// (<c>variants[].mix_percent</c>) into one series of finite percentage sectors.
    /// Missing/unparseable percentages are skipped, never zero-filled.
    /// </summary>
    private static ChartSpec? TryBuildShareOrMixPie(IReadOnlyList<JsonElement> payloads, string requestedType)
    {
        string type = string.Equals(requestedType, "donut", StringComparison.OrdinalIgnoreCase) ? "donut" : "pie";

        foreach (JsonElement payload in payloads)
        {
            if (payload.ValueKind != JsonValueKind.Object)
                continue;

            // Variant mix: variants[].variant + mix_percent.
            if (payload.TryGetProperty("variants", out JsonElement variants) && variants.ValueKind == JsonValueKind.Array)
            {
                var points = new List<ChartDataPoint>();
                foreach (JsonElement v in variants.EnumerateArray())
                {
                    if (v.ValueKind != JsonValueKind.Object) continue;
                    string? label = ReadNestedString(v, "variant");
                    if (string.IsNullOrWhiteSpace(label)) continue;
                    if (!TryReadDouble(v, "mix_percent", out double mix) || !double.IsFinite(mix)) continue;
                    points.Add(new ChartDataPoint { X = label, Y = Math.Round(mix, 1) });
                }
                if (points.Count >= 2)
                {
                    string brand = ReadBrand(payload) ?? "Variant";
                    return new ChartSpec
                    {
                        Type = type,
                        Title = $"{brand} Variant Mix",
                        Data = [new ChartSeries { Legend = "Variant Mix %", Values = points }],
                    };
                }
            }

            // Market share: share_data[].brand + share_percent.
            if (payload.TryGetProperty("share_data", out JsonElement shareData) && shareData.ValueKind == JsonValueKind.Array)
            {
                var points = new List<ChartDataPoint>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonElement s in shareData.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;
                    string? label = ReadNestedString(s, "brand");
                    if (string.IsNullOrWhiteSpace(label) || !seen.Add(label)) continue;
                    if (!TryReadDouble(s, "share_percent", out double share) || !double.IsFinite(share)) continue;
                    points.Add(new ChartDataPoint { X = label, Y = Math.Round(share, 1) });
                }
                if (points.Count >= 2)
                {
                    return new ChartSpec
                    {
                        Type = type,
                        Title = "Market Share Breakdown",
                        Data = [new ChartSeries { Legend = "Market Share %", Values = points }],
                    };
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Build a per-brand depletion-stats table from portfolio-depletion payloads. The
    /// compacted <c>GetPortfolioDepletionStats</c> response flattens per-brand metrics on
    /// the row; the raw response nests them under <c>metrics</c>. Both shapes are read.
    /// When the caller (e.g. the home-improvement table prompt) fans out per region, each
    /// portfolio payload contributes its region's brand rows and the table's X label
    /// becomes <c>Brand — Region</c> so distinct region rows don't collide. Missing/
    /// unparseable numeric cells are dropped, never zero-filled. Non-numeric status
    /// values are excluded (the ChartSpec contract requires finite Y). The renderer's
    /// table falls back to <c>—</c> for absent cells so a brand with only two of three
    /// metrics still renders with real data plus a placeholder.
    /// </summary>
    private static ChartSpec? TryBuildDepletionStatsTable(IReadOnlyList<JsonElement> payloads)
    {
        var depletionsYoy = new List<ChartDataPoint>();
        var sellThroughYoy = new List<ChartDataPoint>();
        var inventoryWeeks = new List<ChartDataPoint>();
        var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contributingRegions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement payload in payloads)
        {
            if (payload.ValueKind != JsonValueKind.Object)
                continue;
            if (!payload.TryGetProperty("brands", out JsonElement brands) || brands.ValueKind != JsonValueKind.Array)
                continue;

            string? payloadRegion = payload.TryGetProperty("region", out JsonElement rEl)
                && rEl.ValueKind == JsonValueKind.String ? rEl.GetString() : null;
            if (!string.IsNullOrWhiteSpace(payloadRegion))
                contributingRegions.Add(payloadRegion);

            foreach (JsonElement brand in brands.EnumerateArray())
            {
                if (brand.ValueKind != JsonValueKind.Object)
                    continue;
                string? name = ReadNestedString(brand, "brand");
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                string? rowRegion = ReadNestedString(brand, "region") ?? payloadRegion;

                JsonElement source = brand;
                if (brand.TryGetProperty("metrics", out JsonElement metrics)
                    && metrics.ValueKind == JsonValueKind.Object
                    && !brand.TryGetProperty("depletions_yoy", out _))
                {
                    source = metrics;
                }

                string label = string.IsNullOrWhiteSpace(rowRegion) ? name : $"{name} — {rowRegion}";
                if (!seenLabels.Add(label))
                    continue;

                if (TryReadPercent(source, "depletions_yoy", out double depYoy) && double.IsFinite(depYoy))
                    depletionsYoy.Add(new ChartDataPoint { X = label, Y = Math.Round(depYoy, 1) });

                if (TryReadPercent(source, "sell_through_yoy", out double sellYoy) && double.IsFinite(sellYoy))
                    sellThroughYoy.Add(new ChartDataPoint { X = label, Y = Math.Round(sellYoy, 1) });

                if (TryReadDouble(source, "inventory_weeks_on_hand", out double weeks) && double.IsFinite(weeks))
                    inventoryWeeks.Add(new ChartDataPoint { X = label, Y = Math.Round(weeks, 1) });
            }
        }

        var series = new List<ChartSeries>();
        if (depletionsYoy.Count > 0)
            series.Add(new ChartSeries { Legend = "Depletions YoY %", Values = depletionsYoy });
        if (sellThroughYoy.Count > 0)
            series.Add(new ChartSeries { Legend = "Sell-Through YoY %", Values = sellThroughYoy });
        if (inventoryWeeks.Count > 0)
            series.Add(new ChartSeries { Legend = "Inventory (weeks on hand)", Values = inventoryWeeks });

        int marks = series.Sum(s => s.Values.Count);
        if (series.Count == 0 || seenLabels.Count < 2 || marks < 2)
            return null;

        string regionAxisLabel = contributingRegions.Count > 0 ? "Brand / Region" : "Brand";
        string titleSuffix = contributingRegions.Count switch
        {
            0 => string.Empty,
            1 => $" — {contributingRegions.First()}",
            _ => " by Region",
        };

        return new ChartSpec
        {
            Type = "table",
            Title = $"Depletion Stats{titleSuffix}",
            XAxisTitle = regionAxisLabel,
            YAxisTitle = "Depletion Stats",
            Data = series,
        };
    }

    /// <summary>
    /// Build a single-value gauge (0–100) from an inventory or supply-health payload.
    /// Inventory health = healthy SKUs / total SKUs * 100; supply-health falls back to
    /// the composite avg_fill_rate. Never fabricated — derived from the tool figures.
    /// </summary>
    private static ChartSpec? TryBuildGauge(IReadOnlyList<JsonElement> payloads)
    {
        foreach (JsonElement payload in payloads)
        {
            if (payload.ValueKind != JsonValueKind.Object)
                continue;

            // Inventory-levels fingerprint: total_items + status_breakdown.
            if (payload.TryGetProperty("total_items", out JsonElement totalItems)
                && totalItems.ValueKind == JsonValueKind.Number
                && payload.TryGetProperty("status_breakdown", out JsonElement breakdown)
                && breakdown.ValueKind == JsonValueKind.Object
                && breakdown.TryGetProperty("healthy", out JsonElement healthy)
                && healthy.ValueKind == JsonValueKind.Number)
            {
                int total = totalItems.GetInt32();
                if (total > 0)
                {
                    double score = Math.Round(healthy.GetInt32() * 100.0 / total, 1);
                    return BuildGaugeSpec(payload, score);
                }
            }

            // Supply-health-summary fingerprint: overall_status + inventory_health + details.
            if (payload.TryGetProperty("inventory_health", out _)
                && payload.TryGetProperty("details", out JsonElement details)
                && details.ValueKind == JsonValueKind.Object
                && TryReadDouble(details, "avg_fill_rate", out double fill)
                && double.IsFinite(fill))
            {
                return BuildGaugeSpec(payload, Math.Clamp(Math.Round(fill, 1), 0, 100));
            }
        }

        return null;
    }

    private static ChartSpec BuildGaugeSpec(JsonElement payload, double score)
    {
        string? brand = ReadBrand(payload);
        string? region = ReadRegion(payload);
        string label = (brand, region) switch
        {
            (not null, not null) => $"{brand} — {region}",
            (not null, null) => brand,
            (null, not null) => region,
            _ => "Inventory Health"
        };
        string title = brand is null ? "Inventory Health" : $"{brand} Inventory Health";
        if (region is not null)
            title += $" — {region}";

        return new ChartSpec
        {
            Type = "gauge",
            Title = title,
            Data =
            [
                new ChartSeries
                {
                    Legend = "Inventory Health",
                    Values = [new ChartDataPoint { X = label, Y = score }]
                }
            ]
        };
    }

    private static string NormalizeBarType(string? requestedType) => requestedType switch
    {
        "groupedBar" or "stackedBar" or "horizontalBar" or "bar" => requestedType,
        _ => "bar"
    };

    private static string? ReadBrand(JsonElement payload) => ReadNestedString(payload, "brand");

    private static string? ReadRegion(JsonElement payload) => ReadNestedString(payload, "region");

    /// <summary>
    /// Reads a string <paramref name="field"/> from the payload's <c>filters</c> or
    /// <c>filters_applied</c> object, or from the top level, in that order.
    /// </summary>
    private static string? ReadNestedString(JsonElement payload, string field)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;

        foreach (string container in (ReadOnlySpan<string>)["filters", "filters_applied"])
        {
            if (payload.TryGetProperty(container, out JsonElement scope)
                && scope.ValueKind == JsonValueKind.Object
                && scope.TryGetProperty(field, out JsonElement scoped)
                && scoped.ValueKind == JsonValueKind.String)
            {
                return scoped.GetString();
            }
        }

        return payload.TryGetProperty(field, out JsonElement top) && top.ValueKind == JsonValueKind.String
            ? top.GetString()
            : null;
    }

    private static bool TryReadDouble(JsonElement obj, string property, out double value)
    {
        value = 0;
        if (obj.TryGetProperty(property, out JsonElement el))
        {
            if (el.ValueKind == JsonValueKind.Number)
                return el.TryGetDouble(out value);
            if (el.ValueKind == JsonValueKind.String)
                return double.TryParse(el.GetString(), out value);
        }
        return false;
    }

    /// <summary>
    /// Reads a percentage metric that may be a number (3.2) or a formatted string
    /// ("+3.2%", "-1.5 %"). Returns false when the property is absent or unparseable
    /// so callers can EXCLUDE the entity rather than substitute a fabricated zero.
    /// </summary>
    private static bool TryReadPercent(JsonElement obj, string property, out double value)
    {
        value = 0;
        if (!obj.TryGetProperty(property, out JsonElement el))
            return false;

        if (el.ValueKind == JsonValueKind.Number)
            return el.TryGetDouble(out value);

        if (el.ValueKind == JsonValueKind.String)
        {
            string? raw = el.GetString();
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            string cleaned = raw.Replace("%", string.Empty).Replace("+", string.Empty).Trim();
            return double.TryParse(cleaned, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        return false;
    }
}

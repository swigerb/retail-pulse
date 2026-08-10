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
    {
        chart = null;
        List<JsonElement> payloads = CollectToolPayloads(response);
        if (payloads.Count == 0)
        {
            return false;
        }

        bool gaugeFirst = string.Equals(requestedType, "gauge", StringComparison.OrdinalIgnoreCase);

        ChartSpec? built = gaugeFirst
            ? TryBuildGauge(payloads) ?? TryBuildDemandBar(payloads, requestedType)
            : TryBuildDemandBar(payloads, requestedType) ?? TryBuildGauge(payloads);

        if (built is not null && ChartSpecValidator.TryGetRenderable(built, out ChartSpec? renderable) && renderable is not null)
        {
            chart = renderable;
            return true;
        }

        return false;
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
}

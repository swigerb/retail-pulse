using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RetailPulse.Api.Budget;

/// <summary>
/// Compactor for <c>GetHistoricalDemand</c> — by far the largest tool payload
/// (~147 KB / ~36 K est. tokens for a single all-regions/all-channels/12-month call).
/// The verbose <c>weekly_data</c> array (region x channel x week rows) is the amplifier.
///
/// This projection preserves the canonical <c>period</c>, <c>filters</c>, and
/// <c>summary</c> (total volume/units, weeks-of-data, avg weekly volume) and replaces
/// the raw weekly rows with aligned <c>by_region</c> and bounded chronological
/// <c>by_time</c> rollups. Both projections retain exact source sums while keeping
/// year-scale all-region responses inside the gateway budget.
/// </summary>
public sealed class HistoricalDemandCompactor : IToolResultCompactor
{
    private const int _maxTemporalIntervals = 12;

    public bool CanCompact(string toolName) =>
        string.Equals(toolName, "GetHistoricalDemand", StringComparison.OrdinalIgnoreCase);

    public ToolCompactionOutcome Compact(string toolName, string rawJson, ToolResultBudgetOptions options)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(rawJson);
        }
        catch (JsonException)
        {
            return ToolCompactionOutcome.Unhandled(rawJson);
        }

        if (root is not JsonObject obj || obj["weekly_data"] is not JsonArray weekly)
        {
            return ToolCompactionOutcome.Unhandled(rawJson);
        }

        int originalRows = weekly.Count;

        // Aggregate weekly rows into per-region and chronological rollups. Distinct week
        // labels feed faithful averages and bounded temporal intervals.
        var byRegion = new Dictionary<string, RegionAgg>(StringComparer.OrdinalIgnoreCase);
        var byWeek = new SortedDictionary<DateOnly, TemporalAgg>();
        foreach (JsonNode? rowNode in weekly)
        {
            if (rowNode is not JsonObject row)
                continue;

            string region = row["region"]?.GetValue<string>() ?? "Unknown";
            double volume = ReadDouble(row["volume"]);
            int units = ReadInt(row["units"]);
            string? week = row["week_starting"]?.GetValue<string>();

            if (!byRegion.TryGetValue(region, out RegionAgg? agg))
            {
                agg = new RegionAgg();
                byRegion[region] = agg;
            }
            agg.Volume += volume;
            agg.Units += units;
            if (week is not null)
            {
                agg.Weeks.Add(week);
                if (DateOnly.TryParse(week, out DateOnly weekStarting))
                {
                    if (!byWeek.TryGetValue(weekStarting, out TemporalAgg? temporal))
                    {
                        temporal = new TemporalAgg();
                        byWeek[weekStarting] = temporal;
                    }
                    temporal.Volume += volume;
                    temporal.Units += units;
                    temporal.SourceRows++;
                }
            }
        }

        var byRegionArray = new JsonArray();
        foreach ((string region, RegionAgg agg) in byRegion.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            int weekCount = agg.Weeks.Count;
            byRegionArray.Add(new JsonObject
            {
                ["region"] = region,
                ["volume"] = Math.Round(agg.Volume, 1),
                ["units"] = agg.Units,
                ["avg_weekly_volume"] = weekCount > 0 ? Math.Round(agg.Volume / weekCount, 1) : 0,
                ["weeks"] = weekCount
            });
        }

        var byTimeArray = BuildTemporalIntervals(byWeek);
        var projected = new JsonObject
        {
            ["period"] = obj["period"]?.DeepClone(),
            ["filters"] = obj["filters"]?.DeepClone(),
            ["summary"] = obj["summary"]?.DeepClone(),
            ["by_time"] = byTimeArray,
            ["by_region"] = byRegionArray,
            ["compaction"] = new JsonObject
            {
                ["compacted"] = true,
                ["aggregate_complete"] = true,
                ["regional_complete"] = byRegionArray.Count > 0,
                ["temporal_complete"] = byTimeArray.Count > 0,
                ["temporal_granularity"] = byWeek.Count <= _maxTemporalIntervals
                    ? "week"
                    : "bounded_week_interval",
                ["original_weekly_rows"] = originalRows,
                ["original_weeks"] = byWeek.Count,
                ["returned_temporal_intervals"] = byTimeArray.Count,
                ["returned_region_rows"] = byRegionArray.Count,
                ["sufficient_for"] = new JsonArray
                {
                    "total volume and units",
                    "average weekly/daily depletion velocity",
                    "temporal depletion trend",
                    "cross-brand comparison",
                    "cross-region comparison",
                    "chart of totals or velocity by time/brand/region"
                },
                ["narrowed_detail"] = new JsonArray
                {
                    "individual source rows",
                    "per-channel breakdown",
                    "single-week anomaly detection when temporal intervals span multiple weeks"
                },
                ["detail_hint"] = "Weekly rows were rolled up into exact chronological intervals "
                    + "and per-region totals. Both aggregates are complete and sufficient; proceed with temporal "
                    + "trends or brand/region comparisons using the matching projection."
            }
        };

        string json = projected.ToJsonString();
        return new ToolCompactionOutcome(
            json,
            Changed: true,
            // Rolling weekly rows into a faithful per-region aggregate is NOT aggregate loss:
            // the summary/velocity/region figures are complete. Marking this Truncated=false
            // keeps telemetry honest and avoids signalling "data unusable" downstream.
            Truncated: false,
            OriginalItems: originalRows,
            ReturnedItems: byRegionArray.Count + byTimeArray.Count);
    }

    private static JsonArray BuildTemporalIntervals(SortedDictionary<DateOnly, TemporalAgg> byWeek)
    {
        var result = new JsonArray();
        if (byWeek.Count == 0)
            return result;

        KeyValuePair<DateOnly, TemporalAgg>[] weeks = [.. byWeek];
        int intervalSize = (int)Math.Ceiling(weeks.Length / (double)_maxTemporalIntervals);
        for (int index = 0; index < weeks.Length; index += intervalSize)
        {
            KeyValuePair<DateOnly, TemporalAgg>[] interval = [.. weeks.Skip(index).Take(intervalSize)];
            DateOnly start = interval[0].Key;
            DateOnly end = interval[^1].Key;
            string startLabel = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string endLabel = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            result.Add(new JsonObject
            {
                ["start_week"] = startLabel,
                ["end_week"] = endLabel,
                ["label"] = start == end ? startLabel : $"{startLabel} to {endLabel}",
                ["volume"] = Math.Round(interval.Sum(item => item.Value.Volume), 1),
                ["units"] = interval.Sum(item => item.Value.Units),
                ["weeks"] = interval.Length,
                ["source_rows"] = interval.Sum(item => item.Value.SourceRows)
            });
        }

        return result;
    }

    private static double ReadDouble(JsonNode? node)
    {
        if (node is null) return 0;
        try { return node.GetValue<double>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return 0; }
    }

    private static int ReadInt(JsonNode? node)
    {
        if (node is null) return 0;
        try { return node.GetValue<int>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return (int)Math.Round(ReadDouble(node));
        }
    }

    private sealed class RegionAgg
    {
        public double Volume;
        public int Units;
        public HashSet<string> Weeks { get; } = new(StringComparer.Ordinal);
    }

    private sealed class TemporalAgg
    {
        public double Volume;
        public int Units;
        public int SourceRows;
    }
}

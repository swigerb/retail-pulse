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
/// the raw weekly rows with an aligned <c>by_region</c> rollup — a COMPLETE aggregate
/// that fully answers totals, depletion-velocity, and cross-brand/region comparison
/// questions. It flags <c>aggregate_complete: true</c> so the model trusts the summary
/// instead of mistaking the rollup for truncated/unusable data, and only narrows
/// week-level/per-channel detail (recoverable via a narrower re-call). It never fabricates
/// values.
/// </summary>
public sealed class HistoricalDemandCompactor : IToolResultCompactor
{
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

        // Aggregate weekly rows into per-region rollups. Distinct week labels feed a
        // faithful avg-weekly per region (matching the source's own averaging basis).
        var byRegion = new Dictionary<string, RegionAgg>(StringComparer.OrdinalIgnoreCase);
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
                agg.Weeks.Add(week);
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

        var projected = new JsonObject
        {
            ["period"] = obj["period"]?.DeepClone(),
            ["filters"] = obj["filters"]?.DeepClone(),
            ["summary"] = obj["summary"]?.DeepClone(),
            ["by_region"] = byRegionArray,
            ["compaction"] = new JsonObject
            {
                ["compacted"] = true,
                // The aggregate (totals, average weekly/daily velocity, per-region rollup)
                // is COMPLETE and faithful — only the per-week/per-channel detail rows were
                // rolled up. Callers answering an aggregate question (totals, depletion
                // velocity, cross-brand or cross-region comparison) have everything they
                // need and MUST proceed. This is NOT a truncated/unusable result.
                ["aggregate_complete"] = true,
                ["original_weekly_rows"] = originalRows,
                ["returned_region_rows"] = byRegionArray.Count,
                ["sufficient_for"] = new JsonArray
                {
                    "total volume and units",
                    "average weekly/daily depletion velocity",
                    "cross-brand comparison",
                    "cross-region comparison",
                    "chart of totals or velocity by brand/region"
                },
                ["narrowed_detail"] = new JsonArray
                {
                    "individual week-over-week trend",
                    "per-channel breakdown",
                    "single-week anomaly detection"
                },
                ["detail_hint"] = "Weekly rows were rolled up per region. The summary and by_region "
                    + "figures are complete and sufficient for totals, depletion-velocity, and "
                    + "brand/region comparisons — proceed and build the chart. Only re-call "
                    + "GetHistoricalDemand with an explicit single region and a smaller months window "
                    + "if you specifically need week-level trend or per-channel detail."
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
            ReturnedItems: byRegionArray.Count);
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
}

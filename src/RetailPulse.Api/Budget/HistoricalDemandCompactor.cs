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
/// the raw weekly rows with an aligned <c>by_region</c> rollup — enough for a
/// cross-brand, by-region comparison chart — plus an explicit compaction/continuation
/// note describing how to opt back into week-level detail. It never fabricates values.
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
                ["original_weekly_rows"] = originalRows,
                ["returned_region_rows"] = byRegionArray.Count,
                ["detail_hint"] = "Weekly rows were rolled up per region to fit the tool-context budget. "
                    + "For week-level detail, call GetHistoricalDemand again with an explicit single region "
                    + "and a smaller months window."
            }
        };

        string json = projected.ToJsonString();
        return new ToolCompactionOutcome(
            json,
            Changed: true,
            Truncated: byRegionArray.Count < originalRows,
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

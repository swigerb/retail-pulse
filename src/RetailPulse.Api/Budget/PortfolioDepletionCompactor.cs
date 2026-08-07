using System.Text.Json;
using System.Text.Json.Nodes;

namespace RetailPulse.Api.Budget;

/// <summary>
/// Compactor for <c>GetPortfolioDepletionStats</c>. The portfolio payload holds one
/// nested depletion record per brand; the verbose <c>sentiment_summary</c> prose on each
/// brand dominates the size. This projection keeps the canonical <c>region</c>,
/// <c>period</c>, and <c>brandCount</c> plus a compact per-brand metrics row (YoY
/// depletions/sell-through, inventory weeks, status) — everything a cross-brand
/// comparison or ranking chart needs — and drops the per-brand narrative, recording that
/// it did. It never fabricates values.
/// </summary>
public sealed class PortfolioDepletionCompactor : IToolResultCompactor
{
    public bool CanCompact(string toolName) =>
        string.Equals(toolName, "GetPortfolioDepletionStats", StringComparison.OrdinalIgnoreCase);

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

        if (root is not JsonObject obj || obj["brands"] is not JsonArray brands)
        {
            return ToolCompactionOutcome.Unhandled(rawJson);
        }

        var compactBrands = new JsonArray();
        foreach (JsonNode? brandNode in brands)
        {
            if (brandNode is not JsonObject brand)
                continue;

            var row = new JsonObject
            {
                ["brand"] = brand["brand"]?.DeepClone(),
                ["region"] = brand["region"]?.DeepClone()
            };

            // Flatten the nested metrics object; skip the verbose sentiment_summary.
            if (brand["metrics"] is JsonObject metrics)
            {
                row["depletions_yoy"] = metrics["depletions_yoy"]?.DeepClone();
                row["sell_through_yoy"] = metrics["sell_through_yoy"]?.DeepClone();
                row["inventory_weeks_on_hand"] = metrics["inventory_weeks_on_hand"]?.DeepClone();
                row["status"] = metrics["status"]?.DeepClone();
            }

            compactBrands.Add(row);
        }

        var projected = new JsonObject
        {
            ["region"] = obj["region"]?.DeepClone(),
            ["period"] = obj["period"]?.DeepClone(),
            ["brandCount"] = obj["brandCount"]?.DeepClone() ?? compactBrands.Count,
            ["brands"] = compactBrands,
            ["compaction"] = new JsonObject
            {
                ["compacted"] = true,
                ["note"] = "Per-brand sentiment narrative was removed to fit the tool-context budget. "
                    + "Call GetDepletionStats for a single brand to retrieve its full sentiment summary."
            }
        };

        return new ToolCompactionOutcome(
            projected.ToJsonString(),
            Changed: true,
            Truncated: false,
            OriginalItems: brands.Count,
            ReturnedItems: compactBrands.Count);
    }
}

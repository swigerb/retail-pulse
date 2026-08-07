using System.Text.Json;
using System.Text.Json.Nodes;

namespace RetailPulse.Api.Budget;

/// <summary>
/// Last-resort generic compactor. When no tool-specific summarizer applies, this trims
/// the single largest JSON array in the payload to <see cref="ToolResultBudgetOptions.MaxArrayItems"/>
/// elements and attaches explicit continuation metadata (<c>truncated</c>, original/returned
/// counts, a drill-down hint). It always emits valid JSON and never silently drops data
/// without recording that it did.
/// </summary>
public sealed class GenericArrayCompactor : IToolResultCompactor
{
    // Handles any tool — used as the fallback, so CanCompact is always true. The registry
    // consults tool-specific compactors first.
    public bool CanCompact(string toolName) => true;

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

        JsonArray? largest = null;
        FindLargestArray(root, ref largest);

        if (largest is null || largest.Count <= options.MaxArrayItems)
        {
            // No oversized array to trim — signal unhandled so the orchestrator can
            // apply a hard character clip if the payload is still over budget.
            return ToolCompactionOutcome.Unhandled(rawJson);
        }

        int originalCount = largest.Count;

        // Trim in place, keeping the leading window (most tools order most-relevant first).
        while (largest.Count > options.MaxArrayItems)
        {
            largest.RemoveAt(largest.Count - 1);
        }

        var truncationNote = new JsonObject
        {
            ["truncated"] = true,
            ["original_count"] = originalCount,
            ["returned_count"] = largest.Count,
            ["hint"] = $"Result trimmed to the first {largest.Count} of {originalCount} items to fit the "
                + "tool-context budget. Re-call the tool with a narrower filter for the remaining items."
        };

        JsonNode wrapped;
        if (root is JsonObject rootObj)
        {
            rootObj["_truncation"] = truncationNote;
            wrapped = rootObj;
        }
        else
        {
            // Root itself was the (array) payload — wrap so the metadata has a home.
            wrapped = new JsonObject
            {
                ["items"] = root,
                ["_truncation"] = truncationNote
            };
        }

        return new ToolCompactionOutcome(
            wrapped.ToJsonString(),
            Changed: true,
            Truncated: true,
            OriginalItems: originalCount,
            ReturnedItems: largest.Count);
    }

    private static void FindLargestArray(JsonNode? node, ref JsonArray? largest)
    {
        switch (node)
        {
            case JsonArray arr:
                if (largest is null || arr.Count > largest.Count)
                    largest = arr;
                foreach (JsonNode? child in arr)
                    FindLargestArray(child, ref largest);
                break;
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> kv in obj)
                    FindLargestArray(kv.Value, ref largest);
                break;
            default:
                break;
        }
    }
}

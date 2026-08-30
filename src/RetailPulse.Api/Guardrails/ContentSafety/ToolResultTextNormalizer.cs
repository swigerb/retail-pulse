using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RetailPulse.Api.Guardrails.ContentSafety;

/// <summary>
/// Renders a serialized tool result as line-oriented natural text before it is
/// handed to Azure AI Content Safety.
/// </summary>
/// <remarks>
/// <para>
/// Content Safety text moderation is trained on conversational prose. Raw JSON
/// is not prose. A dense run of braces, quotes, commas, and camelCase keys
/// carries no sentence structure, and the classifier scores it out of
/// distribution. That is how a <c>GetStorePerformance</c> payload of store
/// revenue, targets, and regions came back as sexual content at high severity
/// (#244), and why the first mitigation could only guess at which token had
/// tripped it.
/// </para>
/// <para>
/// This is a presentation change, not a policy change. Every scalar in the
/// document is preserved and every property name is emitted alongside its
/// value, so the same all-categories harm scan runs over exactly the same
/// content it saw before. Nothing is exempted, sampled, truncated, or skipped,
/// and there is deliberately no notion of a "trusted" tool that receives a
/// weaker scan. <c>ToolResultTextNormalizerTests.Normalize_PreservesEveryScalarAndPropertyName</c>
/// is the executable statement of that guarantee.
/// </para>
/// <para>
/// The transform is fail-safe in both directions: a payload that is not JSON,
/// or one that renders to nothing, is scanned verbatim rather than scanned
/// partially.
/// </para>
/// </remarks>
public static class ToolResultTextNormalizer
{
    /// <summary>
    /// Depth beyond which a subtree is emitted as raw JSON rather than walked.
    /// A payload nested this deeply is pathological, and emitting it whole keeps
    /// it inside the scan instead of dropping it.
    /// </summary>
    private const int _maxDepth = 32;

    /// <summary>
    /// Returns the text that should be scanned for <paramref name="toolResultJson"/>.
    /// </summary>
    public static string Normalize(string toolResultJson)
    {
        if (string.IsNullOrWhiteSpace(toolResultJson))
        {
            return toolResultJson;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(toolResultJson);
        }
        catch (JsonException)
        {
            // Already unstructured. Scan precisely what the caller produced.
            return toolResultJson;
        }

        if (root is null)
        {
            return toolResultJson;
        }

        var builder = new StringBuilder(toolResultJson.Length);
        Render(root, label: null, builder, depth: 0);

        string rendered = builder.ToString().Trim();

        // Scanning a rendering that lost the payload would be a hole in the
        // guardrail, so an empty render falls back to the original text.
        return rendered.Length == 0 ? toolResultJson : rendered;
    }

    private static void Render(JsonNode? node, string? label, StringBuilder builder, int depth)
    {
        if (depth > _maxDepth)
        {
            if (node is not null)
            {
                AppendLine(builder, label, node.ToJsonString());
            }
            return;
        }

        switch (node)
        {
            case JsonObject obj:
                // The key naming a container is the only place that name
                // appears, so it becomes a heading rather than being dropped.
                // An attacker-controlled property name is content too.
                AppendHeading(builder, label);
                foreach (KeyValuePair<string, JsonNode?> property in obj)
                {
                    Render(property.Value, property.Key, builder, depth + 1);
                }
                break;

            case JsonArray array:
                bool holdsContainers = array.Any(item => item is JsonObject or JsonArray);
                if (holdsContainers || array.Count == 0)
                {
                    AppendHeading(builder, label);
                }

                bool first = true;
                foreach (JsonNode? item in array)
                {
                    // A blank line between records keeps one store, brand, or
                    // review from running into the next as a single sentence.
                    if (!first && item is JsonObject)
                    {
                        builder.Append('\n');
                    }

                    // Containers announce their own contents. Only scalars need
                    // the array's key carried down so the value keeps its name.
                    Render(item, item is JsonObject or JsonArray ? null : label, builder, depth + 1);
                    first = false;
                }
                break;

            case JsonValue value:
                AppendLine(builder, label, ValueText(value));
                break;

            case null:
                // A JSON null still carries its property name, which is content
                // the scan is entitled to see.
                AppendLine(builder, label, string.Empty);
                break;

            default:
                // Any future JsonNode kind must still reach the scanner rather
                // than fall out of the walk unscanned.
                AppendLine(builder, label, node.ToJsonString());
                break;
        }
    }

    private static string ValueText(JsonValue value) =>
        value.TryGetValue(out string? text) ? text ?? string.Empty : value.ToJsonString();

    private static void AppendHeading(StringBuilder builder, string? label)
    {
        if (!string.IsNullOrEmpty(label))
        {
            builder.Append(Humanize(label)).Append(":\n");
        }
    }

    private static void AppendLine(StringBuilder builder, string? label, string text)
    {
        if (!string.IsNullOrEmpty(label))
        {
            builder.Append(Humanize(label)).Append(": ");
        }

        builder.Append(text).Append('\n');
    }

    /// <summary>
    /// Turns a property name into a readable label: <c>performanceIndex</c>
    /// becomes <c>Performance Index</c>, <c>store_name</c> becomes
    /// <c>Store Name</c>. Only separators and the first letter of each word
    /// change, so no character of the original key is lost.
    /// </summary>
    internal static string Humanize(string key)
    {
        var builder = new StringBuilder(key.Length + 8);

        foreach (char c in key)
        {
            if (c is '_' or '-' or '.')
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }
                continue;
            }

            // Split camelCase, but leave runs of capitals such as KPI intact.
            if (char.IsUpper(c) && builder.Length > 0 && builder[^1] != ' ' && !char.IsUpper(builder[^1]))
            {
                builder.Append(' ');
            }

            builder.Append(c);
        }

        // Capitalise the first letter of every word so camelCase and snake_case
        // keys produce the same label shape.
        for (int i = 0; i < builder.Length; i++)
        {
            if (i == 0 || builder[i - 1] == ' ')
            {
                builder[i] = char.ToUpperInvariant(builder[i]);
            }
        }

        string spaced = builder.ToString().Trim();
        return spaced.Length == 0 ? key : spaced;
    }
}

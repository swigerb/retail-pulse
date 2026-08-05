using System.Text;
using System.Text.RegularExpressions;
using RetailPulse.Api.Charts;
using RetailPulse.Contracts;

namespace RetailPulse.Api.Agents;

/// <summary>
/// Inline chart recovery for the agent reply text.
///
/// Some models echo the CreateChart payload as raw JSON inside their prose
/// answer instead of (or in addition to) a clean tool call, and occasionally
/// emit it using a non-canonical, Chart.js-style schema that the tool path
/// cannot bind to <see cref="ChartSpec"/>. Either way the user sees a wall of
/// raw JSON and no chart.
///
/// This pass extracts any chart-spec JSON embedded in the reply, normalizes the
/// realistic schema variations into a canonical <see cref="ChartSpec"/> (via
/// <see cref="ChartSpecNormalizer"/>), and returns the reply with those JSON
/// blocks removed. Only JSON that actually resolves to a usable chart is
/// stripped — arbitrary or malformed JSON is left in place so nothing
/// legitimate is silently hidden.
/// </summary>
public partial class AgentExecutionPipeline
{
    // Collapses code fences that are left empty after their JSON body was removed
    // (e.g. ```json\n\n``` -> "").
    [GeneratedRegex(@"```(?:json)?\s*```", RegexOptions.IgnoreCase)]
    private static partial Regex EmptyCodeFencePattern();

    // Collapses 3+ consecutive newlines down to a single blank line.
    [GeneratedRegex(@"(\r?\n){3,}")]
    private static partial Regex ExcessiveBlankLinesPattern();

    internal readonly record struct InlineChartExtraction(string Reply, List<ChartSpec> Charts);

    /// <summary>
    /// Merges charts recovered from the reply prose into the charts produced by
    /// the tool path, appending only those inline charts that are not a semantic
    /// duplicate of a chart already present (whether tool-produced or an earlier
    /// inline chart). This preserves a distinct chart the model narrated in prose
    /// alongside a different tool-produced chart, while suppressing the common
    /// case where the model echoes the same chart it already emitted as a tool
    /// call. Deduplication uses <see cref="ChartSpecSemanticComparer"/> so it is
    /// based on chart content, not object identity or serialized form.
    /// </summary>
    internal static List<ChartSpec> MergeInlineCharts(List<ChartSpec> toolCharts, IReadOnlyList<ChartSpec> inlineCharts)
    {
        if (inlineCharts.Count == 0)
        {
            return toolCharts;
        }

        var merged = new List<ChartSpec>(toolCharts);
        foreach (ChartSpec inlineChart in inlineCharts)
        {
            bool isDuplicate = merged.Any(existing => ChartSpecSemanticComparer.Instance.Equals(existing, inlineChart));
            if (!isDuplicate)
            {
                merged.Add(inlineChart);
            }
        }
        return merged;
    }

    /// <summary>
    /// Removes chart-spec JSON blocks embedded in the reply prose and returns the
    /// normalized <see cref="ChartSpec"/> instances recovered from them.
    /// </summary>
    internal static InlineChartExtraction ExtractInlineCharts(string reply)
    {
        if (string.IsNullOrEmpty(reply))
        {
            return new InlineChartExtraction(reply, []);
        }

        var charts = new List<ChartSpec>();
        var removals = new List<(int Start, int End)>();

        foreach ((int start, int end) in FindJsonObjectSpans(reply))
        {
            string candidate = reply[start..end];
            if (ChartSpecNormalizer.TryNormalize(candidate, out ChartSpec? chart) && chart is not null)
            {
                charts.Add(chart);
                removals.Add((start, end));
            }
        }

        if (removals.Count == 0)
        {
            return new InlineChartExtraction(reply, charts);
        }

        var sb = new StringBuilder(reply.Length);
        int cursor = 0;
        foreach ((int rs, int re) in removals)
        {
            if (rs > cursor)
            {
                sb.Append(reply, cursor, rs - cursor);
            }
            cursor = re;
        }
        if (cursor < reply.Length)
        {
            sb.Append(reply, cursor, reply.Length - cursor);
        }

        string cleaned = EmptyCodeFencePattern().Replace(sb.ToString(), string.Empty);
        cleaned = ExcessiveBlankLinesPattern().Replace(cleaned, "\n\n").Trim();

        return new InlineChartExtraction(cleaned, charts);
    }

    /// <summary>
    /// Yields the [start, end) spans of every top-level balanced JSON object in
    /// <paramref name="text"/>, honoring string literals and escapes. Truncated
    /// (never-closed) objects are skipped.
    /// </summary>
    private static IEnumerable<(int Start, int End)> FindJsonObjectSpans(string text)
    {
        int i = 0;
        int n = text.Length;
        while (i < n)
        {
            if (text[i] != '{')
            {
                i++;
                continue;
            }

            int depth = 0;
            bool inString = false;
            bool escaped = false;
            int start = i;
            int j = i;
            for (; j < n; j++)
            {
                char c = text[j];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        j++;
                        break;
                    }
                }
            }

            if (depth == 0 && j > start)
            {
                yield return (start, j);
                i = j;
            }
            else
            {
                // Unbalanced / truncated object — leave it in the prose and move on.
                i = start + 1;
            }
        }
    }
}

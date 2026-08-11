using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Budget;
using RetailPulse.Api.Charts;
using RetailPulse.Contracts;

namespace RetailPulse.Api.Agents;

/// <summary>
/// Required-entity label projection and tool-context telemetry surfacing.
///
/// Publix sweep #76 Group C — chart is emitted but silently drops a brand the user
/// asked for by name (Sierra Gold Tequila missing from the line legend/x-axis; Apex
/// Grill absent from the donut; Pinnacle Hardware / Summit Outdoor absent from the
/// table rows). A partial chart is worse than a fail-closed diagnostic because it
/// looks correct while mis-labelling data. This pass runs after chart fulfillment,
/// derives the set of requested entities from the user message using the tenant
/// roster (no brand literals in code), and — for every chart present — asserts each
/// requested entity appears as a legend, a category (x-axis / row key), or in the
/// title. If any are missing we first try to project them from tool payloads that
/// carry the same brand as a filter; if that fails we drop the mis-labelled chart
/// and append the standard chart-unavailable diagnostic so acceptance is
/// deterministic.
///
/// Also owns <see cref="BuildToolContextTelemetry"/> — the surfacing of the
/// per-request <see cref="RequestToolContext"/> counters onto the response DTO
/// (Publix #76 telemetry gap). Sizes/flags only; never payload content.
/// </summary>
public partial class AgentExecutionPipeline
{
    internal readonly record struct ChartLabelProjectionResult(List<ChartSpec> Charts, string Reply);

    /// <summary>
    /// Enforces that every emitted chart exposes every entity name the user asked
    /// for by name. Silently mis-labelled charts are dropped and replaced with a
    /// deterministic diagnostic. No-op when the user message names no roster entity.
    /// </summary>
    internal ChartLabelProjectionResult EnforceRequiredEntityLabels(
        string? userMessage,
        Microsoft.Extensions.AI.ChatResponse response,
        List<ChartSpec> charts,
        string reply)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || charts.Count == 0)
        {
            return new ChartLabelProjectionResult(charts, reply);
        }

        IReadOnlyList<string> requested = ExtractRequestedEntities(userMessage);
        if (requested.Count == 0)
        {
            return new ChartLabelProjectionResult(charts, reply);
        }

        var kept = new List<ChartSpec>(charts.Count);
        var missingAcrossDrops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool anyDropped = false;

        foreach (ChartSpec chart in charts)
        {
            IReadOnlyList<string> missing = MissingEntities(chart, requested);
            if (missing.Count == 0)
            {
                kept.Add(chart);
                continue;
            }

            // Attempt to project missing labels from tool payloads. For a
            // single-brand line/gauge chart the brand often lives only in the
            // title; if so we clone the chart with the brand as the series
            // legend so the label projection contract is met without changing
            // the underlying marks.
            ChartSpec? projected = TryProjectLabels(chart, missing, response);
            if (projected is not null && MissingEntities(projected, requested).Count == 0)
            {
                _logger.LogInformation(
                    "Chart-labels: projected {Count} required entity label(s) onto {Type} chart '{Title}'.",
                    missing.Count, projected.Type, projected.Title);
                kept.Add(projected);
                continue;
            }

            anyDropped = true;
            foreach (string m in missing) missingAcrossDrops.Add(m);
            _logger.LogWarning(
                "Chart-labels: dropping {Type} chart '{Title}' — missing required entity label(s): {Missing}.",
                chart.Type, chart.Title, string.Join(", ", missing));
        }

        if (!anyDropped)
        {
            return new ChartLabelProjectionResult(kept, reply);
        }

        string diag = BuildMissingLabelsDiagnostic(missingAcrossDrops);
        string updated = string.IsNullOrWhiteSpace(reply) ? diag : $"{reply}\n\n{diag}";
        return new ChartLabelProjectionResult(kept, updated);
    }

    /// <summary>
    /// Surfaces the current <see cref="RequestToolContext"/> counters as
    /// telemetry on the response and as tags on the agent-thought span. Returns
    /// null when no scope is active (test paths that bypass the pipeline).
    /// </summary>
    internal static ToolContextTelemetry? BuildToolContextTelemetry(Activity? thoughtActivity)
    {
        RequestToolContext? ctx = RequestToolContext.Current;
        if (ctx is null) return null;

        var opts = new ToolResultBudgetOptions();
        int effectiveCap = ctx.IsChartIntent
            ? Math.Min(opts.MaxToolCalls, opts.MaxToolCallsForChartIntent)
            : opts.MaxToolCalls;

        var telemetry = new ToolContextTelemetry(
            CumulativeChars: ctx.CumulativeChars,
            DistinctCalls: ctx.DistinctCalls,
            MaxCumulativeChars: opts.MaxCumulativeChars,
            MaxToolCalls: effectiveCap,
            IsChartIntent: ctx.IsChartIntent);

        thoughtActivity?.SetTag("tool_context.cumulative_chars", telemetry.CumulativeChars);
        thoughtActivity?.SetTag("tool_context.distinct_calls", telemetry.DistinctCalls);
        thoughtActivity?.SetTag("tool_context.max_cumulative_chars", telemetry.MaxCumulativeChars);
        thoughtActivity?.SetTag("tool_context.max_tool_calls", telemetry.MaxToolCalls);
        thoughtActivity?.SetTag("tool_context.is_chart_intent", telemetry.IsChartIntent);

        return telemetry;
    }

    /// <summary>
    /// Extracts requested-entity names from the user message by exact
    /// case-insensitive substring match against the tenant catalog. Tenant-generic
    /// — the roster comes from configuration, never from a hardcoded list here.
    /// Longer names are preferred so "Sierra Gold Tequila" wins over "Sierra".
    /// </summary>
    private IReadOnlyList<string> ExtractRequestedEntities(string userMessage)
    {
        if (_tenant.Brands.Count == 0) return [];

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? brand in _tenant.Brands
                     .Select(b => b.Name)
                     .Where(n => !string.IsNullOrWhiteSpace(n))
                     .OrderByDescending(n => n.Length))
        {
            if (userMessage.Contains(brand, StringComparison.OrdinalIgnoreCase) && seen.Add(brand))
            {
                results.Add(brand);
            }
        }
        return results;
    }

    private static IReadOnlyList<string> MissingEntities(ChartSpec chart, IReadOnlyList<string> requested)
    {
        if (chart is null) return requested;

        var missing = new List<string>();
        foreach (string entity in requested)
        {
            if (ChartMentionsEntity(chart, entity)) continue;
            missing.Add(entity);
        }
        return missing;
    }

    private static bool ChartMentionsEntity(ChartSpec chart, string entity)
    {
        if (chart.Title is not null && chart.Title.Contains(entity, StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (ChartSeries s in chart.Data)
        {
            if (s.Legend is not null && s.Legend.Contains(entity, StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (ChartDataPoint p in s.Values)
            {
                if (p.X is not null && p.X.Contains(entity, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Attempts a lossless label rewrite that surfaces missing entities without
    /// altering marks. For a single-series chart whose title already carries the
    /// brand (line/gauge), we promote the brand into the series legend. For any
    /// other shape we return null and let the caller fail closed.
    /// </summary>
    private static ChartSpec? TryProjectLabels(ChartSpec chart, IReadOnlyList<string> missing, Microsoft.Extensions.AI.ChatResponse response)
    {
        // Also consult tool payloads: if a tool result carries the missing brand
        // in its filters, we can safely tag the single series with that legend.
        IReadOnlyList<string> brandsFromPayloads = ReadBrandsFromToolResults(response);

        bool titleHasAll = missing.All(e =>
            chart.Title is not null && chart.Title.Contains(e, StringComparison.OrdinalIgnoreCase));
        bool payloadsHaveAll = missing.All(e =>
            brandsFromPayloads.Any(b => b.Contains(e, StringComparison.OrdinalIgnoreCase)));

        if (chart.Data.Count == 1 && (titleHasAll || payloadsHaveAll))
        {
            string preferred = missing[0];
            ChartSeries original = chart.Data[0];
            var relabelled = new ChartSeries
            {
                Legend = preferred,
                Color = original.Color,
                Values = original.Values,
            };
            return new ChartSpec
            {
                Type = chart.Type,
                Title = chart.Title,
                XAxisTitle = chart.XAxisTitle,
                YAxisTitle = chart.YAxisTitle,
                Data = [relabelled],
            };
        }

        return null;
    }

    private static IReadOnlyList<string> ReadBrandsFromToolResults(Microsoft.Extensions.AI.ChatResponse response)
    {
        var brands = new List<string>();
        foreach (ChatMessage message in response.Messages)
        {
            foreach (AIContent content in message.Contents)
            {
                if (content is not FunctionResultContent frc || frc.Result is null) continue;
                string json = frc.Result as string ?? frc.Result.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(json)) continue;
                if (!json.TrimStart().StartsWith('{')) continue;

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    JsonElement root = doc.RootElement;
                    foreach (string container in (ReadOnlySpan<string>)["filters", "filters_applied"])
                    {
                        if (root.TryGetProperty(container, out JsonElement scope)
                            && scope.ValueKind == JsonValueKind.Object
                            && scope.TryGetProperty("brand", out JsonElement b)
                            && b.ValueKind == JsonValueKind.String
                            && b.GetString() is { Length: > 0 } bs)
                        {
                            brands.Add(bs);
                        }
                    }
                    if (root.TryGetProperty("brand", out JsonElement top)
                        && top.ValueKind == JsonValueKind.String
                        && top.GetString() is { Length: > 0 } ts)
                    {
                        brands.Add(ts);
                    }
                }
                catch (JsonException) { }
            }
        }
        return brands;
    }

    private static string BuildMissingLabelsDiagnostic(IReadOnlyCollection<string> missing) =>
        "⚠️ Chart unavailable: the requested chart did not expose the following entities as "
        + $"legend, category, or title labels: {string.Join(", ", missing)}. A partially labelled "
        + "chart would silently mis-attribute values, so no chart is emitted. Retry with a specific "
        + "brand and region, or confirm the entity exists for this tenant.";
}

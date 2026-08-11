using RetailPulse.Contracts;

namespace RetailPulse.Api.Charts;

/// <summary>
/// Enforces the single "renderable chart" invariant shared by every boundary that
/// can emit a <see cref="ChartSpec"/> (the CreateChart tool, the pipeline's
/// tool-result extraction, and inline prose recovery).
///
/// A chart is renderable only when it has a recognized <c>Type</c>, a non-empty
/// <c>Title</c>, and at least one series carrying at least one finite datapoint.
/// Series with no legend or no finite points are dropped, and non-finite Y values
/// (NaN / ±Infinity) are never bindable — so a chart that would render as an empty
/// card (recognized axes/title but no marks) is rejected here instead of being
/// promoted downstream. Multi-series charts keep their original category (X) labels
/// so legend/category alignment is preserved; values are never fabricated.
/// </summary>
internal static class ChartSpecValidator
{
    private static readonly HashSet<string> _knownChartTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "line", "bar", "groupedBar", "stackedBar", "horizontalBar", "pie", "donut", "gauge", "table",
    };

    /// <summary>
    /// True when <paramref name="chart"/> can be reduced to a renderable chart with
    /// at least one finite datapoint.
    /// </summary>
    public static bool IsRenderable(ChartSpec? chart) => TryGetRenderable(chart, out _);

    /// <summary>
    /// Minimum number of finite marks a chart of the given type must carry to be
    /// meaningful (not just non-empty). A single-value gauge needs 1; a comparison
    /// bar/line needs at least 2 to compare; a pie/donut needs 2 sectors to show a
    /// breakdown. This backs the acceptance invariant "correct semantics, not only
    /// existence" — a one-bar "comparison" or a single-sector pie is rejected.
    /// </summary>
    public static int MinimumMarksForType(string? type) => (type?.ToLowerInvariant()) switch
    {
        "gauge" => 1,
        "pie" or "donut" => 2,
        "line" or "bar" or "groupedbar" or "stackedbar" or "horizontalbar" => 2,
        "table" => 1,
        _ => 1,
    };

    /// <summary>
    /// Renderable AND carries at least <paramref name="minMarks"/> finite marks across
    /// all cleaned series, and at least <paramref name="minSeries"/> legend-bearing
    /// series. Use for acceptance/semantic gating (a chart that renders but is trivially
    /// under-populated — e.g. a grouped bar with a single mark — is rejected). Never
    /// fabricates or reorders data.
    /// </summary>
    public static bool TryGetRenderable(ChartSpec? chart, int minSeries, int minMarks, out ChartSpec? renderable)
    {
        renderable = null;
        if (!TryGetRenderable(chart, out ChartSpec? cleaned) || cleaned is null)
        {
            return false;
        }

        int series = cleaned.Data.Count;
        int marks = cleaned.Data.Sum(s => s.Values.Count(p => p is not null && double.IsFinite(p.Y)));
        if (series < Math.Max(1, minSeries) || marks < Math.Max(1, minMarks))
        {
            return false;
        }

        renderable = cleaned;
        return true;
    }

    /// <summary>
    /// Produces a cleaned chart that contains only legend-bearing series with finite
    /// datapoints. Returns false (and a null chart) when nothing renderable remains,
    /// so callers can surface a diagnostic instead of a blank card. Never fabricates
    /// datapoints or reorders series.
    /// </summary>
    public static bool TryGetRenderable(ChartSpec? chart, out ChartSpec? renderable)
    {
        renderable = null;

        if (chart is null
            || string.IsNullOrWhiteSpace(chart.Type)
            || !_knownChartTypes.Contains(chart.Type)
            || string.IsNullOrWhiteSpace(chart.Title))
        {
            return false;
        }

        var cleanedSeries = new List<ChartSeries>();
        bool changed = false;

        foreach (ChartSeries series in chart.Data)
        {
            if (series is null || string.IsNullOrWhiteSpace(series.Legend))
            {
                changed = true;
                continue;
            }

            var finitePoints = new List<ChartDataPoint>(series.Values.Count);
            foreach (ChartDataPoint point in series.Values)
            {
                if (point is not null && double.IsFinite(point.Y))
                {
                    finitePoints.Add(point);
                }
            }

            if (finitePoints.Count == 0)
            {
                changed = true;
                continue;
            }

            if (finitePoints.Count != series.Values.Count)
            {
                changed = true;
                cleanedSeries.Add(series with { Values = finitePoints });
            }
            else
            {
                cleanedSeries.Add(series);
            }
        }

        if (cleanedSeries.Count == 0)
        {
            return false;
        }

        renderable = changed ? chart with { Data = cleanedSeries } : chart;
        return true;
    }
}

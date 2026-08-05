using System.Globalization;
using System.Text.Json;
using RetailPulse.Contracts;

namespace RetailPulse.Api.Charts;

/// <summary>
/// Normalizes chart JSON emitted by the LLM into the canonical
/// <see cref="ChartSpec"/> shape, tolerating realistic schema variations.
///
/// Models sometimes deviate from the documented <c>ChartSpec</c> schema
/// (<c>data: [{ legend, values: [{ x, y }] }]</c>) and instead emit a
/// Chart.js-style payload (<c>data: { labels, series: [{ name, values }] }</c>),
/// or attach axis titles under an <c>options</c> object, or orient a bar chart
/// horizontally via <c>options.orientation</c>. This normalizer maps those
/// variations onto the one shape the frontend renders.
///
/// It is deliberately strict about what counts as a chart: a recognized chart
/// <c>type</c>, a <c>title</c>, and at least one bindable datapoint are all
/// required. Callers rely on that so they can leave non-chart or unusable JSON
/// untouched instead of silently discarding it.
/// </summary>
internal static class ChartSpecNormalizer
{
    private static readonly HashSet<string> _knownChartTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "line", "bar", "groupedBar", "stackedBar", "horizontalBar", "pie", "donut", "gauge", "table",
    };

    private static readonly string[] _pointXKeys = ["x", "label", "name", "category"];
    private static readonly string[] _pointYKeys = ["y", "value", "count"];

    /// <summary>
    /// Attempts to parse and normalize a JSON string into a canonical
    /// <see cref="ChartSpec"/>. Returns false (and a null chart) when the JSON is
    /// not a recognizable, bindable chart.
    /// </summary>
    public static bool TryNormalize(string json, out ChartSpec? chart)
    {
        chart = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        using (doc)
        {
            return TryNormalize(doc.RootElement, out chart);
        }
    }

    /// <summary>
    /// Normalizes an already-parsed JSON element. Returns false when the element
    /// is not a recognizable, bindable chart.
    /// </summary>
    public static bool TryNormalize(JsonElement root, out ChartSpec? chart)
    {
        chart = null;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!root.TryGetProperty("type", out JsonElement typeEl) || typeEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        string? rawType = typeEl.GetString();
        if (string.IsNullOrWhiteSpace(rawType) || !_knownChartTypes.Contains(rawType))
        {
            return false;
        }

        string? title = GetStringOrNull(root, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        string? xAxis = GetStringOrNull(root, "xAxisTitle");
        string? yAxis = GetStringOrNull(root, "yAxisTitle");
        string? orientation = null;
        if (root.TryGetProperty("options", out JsonElement options) && options.ValueKind == JsonValueKind.Object)
        {
            xAxis ??= GetStringOrNull(options, "xAxisLabel") ?? GetStringOrNull(options, "xAxisTitle");
            yAxis ??= GetStringOrNull(options, "yAxisLabel") ?? GetStringOrNull(options, "yAxisTitle");
            orientation = GetStringOrNull(options, "orientation");
        }

        // Full-config style: axis titles under xAxis/yAxis objects (e.g. xAxis.label).
        if (root.TryGetProperty("xAxis", out JsonElement xAxisEl) && xAxisEl.ValueKind == JsonValueKind.Object)
        {
            xAxis ??= GetStringOrNull(xAxisEl, "label") ?? GetStringOrNull(xAxisEl, "title");
        }
        if (root.TryGetProperty("yAxis", out JsonElement yAxisEl) && yAxisEl.ValueKind == JsonValueKind.Object)
        {
            yAxis ??= GetStringOrNull(yAxisEl, "label") ?? GetStringOrNull(yAxisEl, "title");
        }
        orientation ??= GetStringOrNull(root, "orientation");

        List<ChartSeries> series = ExtractSeries(root, title);
        if (series.Count == 0)
        {
            return false;
        }

        chart = new ChartSpec
        {
            Type = NormalizeChartType(rawType, orientation),
            Title = title,
            XAxisTitle = xAxis,
            YAxisTitle = yAxis,
            Data = series,
        };
        return true;
    }

    private static List<ChartSeries> ExtractSeries(JsonElement root, string chartTitle)
    {
        if (root.TryGetProperty("data", out JsonElement data))
        {
            if (data.ValueKind == JsonValueKind.Array)
            {
                return SeriesFromArray(data);
            }
            if (data.ValueKind == JsonValueKind.Object)
            {
                return SeriesFromLabelledObject(data, chartTitle);
            }
        }

        // Full-config (Chart.js-style) schema: a top-level "series" array paired with
        // labels under xAxis.categories / top-level categories or labels. Each series
        // carries its numbers under "data" or "values".
        return root.TryGetProperty("series", out JsonElement topSeries) && topSeries.ValueKind == JsonValueKind.Array
            ? SeriesFromSeriesArray(topSeries, GatherLabels(root), chartTitle)
            : [];
    }

    // Collects category labels shared by all series, checking the common locations
    // models use: top-level labels/categories, or xAxis.{categories,labels,data}.
    private static List<string> GatherLabels(JsonElement root)
    {
        List<string>? labels = LabelsFrom(root, "labels") ?? LabelsFrom(root, "categories");
        if (labels is null && root.TryGetProperty("xAxis", out JsonElement xAxisEl) && xAxisEl.ValueKind == JsonValueKind.Object)
        {
            labels = LabelsFrom(xAxisEl, "categories") ?? LabelsFrom(xAxisEl, "labels") ?? LabelsFrom(xAxisEl, "data");
        }
        return labels ?? [];
    }

    private static List<string>? LabelsFrom(JsonElement source, string property)
    {
        if (!source.TryGetProperty(property, out JsonElement el) || el.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var labels = new List<string>();
        foreach (JsonElement label in el.EnumerateArray())
        {
            labels.Add(ElementToXString(label));
        }
        return labels;
    }

    // Canonical schema: data is an array of series objects, each with a
    // legend/name and a values array of {x, y} points (or plain numbers).
    private static List<ChartSeries> SeriesFromArray(JsonElement dataArray)
    {
        var result = new List<ChartSeries>();
        foreach (JsonElement element in dataArray.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string legend = GetStringOrNull(element, "legend")
                ?? GetStringOrNull(element, "name")
                ?? GetStringOrNull(element, "label")
                ?? $"Series {result.Count + 1}";
            string? color = GetStringOrNull(element, "color");

            ChartSeries? built = BuildSeries(legend, color, element, []);
            if (built is not null)
            {
                result.Add(built);
            }
        }

        return result;
    }

    // Alternate (Chart.js-style) schema: data is an object with a shared "labels"
    // array and a "series" array of { name, values:[numbers] }. Also tolerates a
    // single-series shape where "labels" is paired directly with "values".
    private static List<ChartSeries> SeriesFromLabelledObject(JsonElement dataObj, string chartTitle)
    {
        var labels = new List<string>();
        if (dataObj.TryGetProperty("labels", out JsonElement labelsEl) && labelsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement label in labelsEl.EnumerateArray())
            {
                labels.Add(ElementToXString(label));
            }
        }

        if (dataObj.TryGetProperty("series", out JsonElement seriesEl) && seriesEl.ValueKind == JsonValueKind.Array)
        {
            return SeriesFromSeriesArray(seriesEl, labels, chartTitle);
        }

        var result = new List<ChartSeries>();
        if (dataObj.TryGetProperty("values", out _))
        {
            // Single implicit series keyed by the chart title.
            ChartSeries? built = BuildSeries(chartTitle, null, dataObj, labels);
            if (built is not null)
            {
                result.Add(built);
            }
        }

        return result;
    }

    // Builds series from a "series" array of { name, values|data:[...] } objects,
    // sharing the provided category labels across every series.
    private static List<ChartSeries> SeriesFromSeriesArray(JsonElement seriesEl, List<string> labels, string chartTitle)
    {
        var result = new List<ChartSeries>();
        foreach (JsonElement s in seriesEl.EnumerateArray())
        {
            if (s.ValueKind == JsonValueKind.Array)
            {
                // A bare array of numbers is an implicit single series.
                ChartSeries? bare = BuildSeriesFromValues(chartTitle, null, s, labels);
                if (bare is not null)
                {
                    result.Add(bare);
                }
                continue;
            }

            if (s.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string legend = GetStringOrNull(s, "name")
                ?? GetStringOrNull(s, "legend")
                ?? GetStringOrNull(s, "label")
                ?? $"Series {result.Count + 1}";
            string? color = GetStringOrNull(s, "color");

            ChartSeries? built = BuildSeries(legend, color, s, labels);
            if (built is not null)
            {
                result.Add(built);
            }
        }

        return result;
    }

    private static ChartSeries? BuildSeries(string legend, string? color, JsonElement seriesObj, List<string> labels)
    {
        bool hasValues = seriesObj.TryGetProperty("values", out JsonElement valuesEl)
            || seriesObj.TryGetProperty("data", out valuesEl);
        return !hasValues ? null : BuildSeriesFromValues(legend, color, valuesEl, labels);
    }

    private static ChartSeries? BuildSeriesFromValues(string legend, string? color, JsonElement valuesEl, List<string> labels)
    {
        if (valuesEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var points = new List<ChartDataPoint>();
        int index = 0;
        foreach (JsonElement v in valuesEl.EnumerateArray())
        {
            string fallbackX = index < labels.Count
                ? labels[index]
                : (index + 1).ToString(CultureInfo.InvariantCulture);

            if (v.ValueKind == JsonValueKind.Object)
            {
                string x = GetPointX(v) ?? fallbackX;
                if (TryGetPointY(v, out double oy))
                {
                    points.Add(new ChartDataPoint { X = x, Y = oy });
                }
            }
            else if (TryElementToDouble(v, out double y))
            {
                points.Add(new ChartDataPoint { X = fallbackX, Y = y });
            }

            index++;
        }

        return points.Count > 0
            ? new ChartSeries { Legend = legend, Color = color, Values = points }
            : null;
    }

    private static string NormalizeChartType(string rawType, string? orientation)
    {
        string t = rawType.Trim().ToLowerInvariant();
        return t == "bar" && string.Equals(orientation, "horizontal", StringComparison.OrdinalIgnoreCase)
            ? "horizontalBar"
            : t switch
            {
                "line" => "line",
                "bar" => "bar",
                "groupedbar" => "groupedBar",
                "stackedbar" => "stackedBar",
                "horizontalbar" => "horizontalBar",
                "pie" => "pie",
                "donut" => "donut",
                "gauge" => "gauge",
                "table" => "table",
                _ => t,
            };
    }

    private static string? GetStringOrNull(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static string? GetPointX(JsonElement point)
    {
        foreach (string key in _pointXKeys)
        {
            if (point.TryGetProperty(key, out JsonElement el) && el.ValueKind != JsonValueKind.Null)
            {
                return ElementToXString(el);
            }
        }
        return null;
    }

    private static bool TryGetPointY(JsonElement point, out double value)
    {
        foreach (string key in _pointYKeys)
        {
            if (point.TryGetProperty(key, out JsonElement el) && TryElementToDouble(el, out value))
            {
                return true;
            }
        }
        value = 0;
        return false;
    }

    private static string ElementToXString(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty
        : e.ValueKind == JsonValueKind.Number ? e.GetDouble().ToString(CultureInfo.InvariantCulture)
        : e.ValueKind == JsonValueKind.True ? "true"
        : e.ValueKind == JsonValueKind.False ? "false"
        : e.ToString();

    private static bool TryElementToDouble(JsonElement e, out double value)
    {
        if (e.ValueKind == JsonValueKind.Number)
        {
            return e.TryGetDouble(out value);
        }
        if (e.ValueKind == JsonValueKind.String)
        {
            return double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
        value = 0;
        return false;
    }
}

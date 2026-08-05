using RetailPulse.Contracts;

namespace RetailPulse.Api.Charts;

/// <summary>
/// Stable, value-based equality for <see cref="ChartSpec"/> used to deduplicate
/// charts recovered from inline prose against charts produced by the tool path.
///
/// <see cref="ChartSpec"/> is a record, but its <c>Data</c> (and nested
/// <c>Values</c>) are <see cref="List{T}"/>s, so the compiler-generated record
/// equality compares those by reference — two structurally identical charts from
/// different sources would never compare equal. Reference identity is therefore
/// useless here, and serialization-based comparison is fragile (property order,
/// numeric formatting, null vs. absent). This comparer instead walks the chart's
/// meaningful fields so a genuine duplicate echo is suppressed while a distinct
/// chart is preserved.
/// </summary>
internal sealed class ChartSpecSemanticComparer : IEqualityComparer<ChartSpec>
{
    // Charts recovered from prose and produced by tools originate from the same
    // numeric parse, but compare Y with a tiny tolerance to stay robust against
    // representation drift rather than relying on exact double equality.
    private const double _valueEpsilon = 1e-9;

    public static readonly ChartSpecSemanticComparer Instance = new();

    public bool Equals(ChartSpec? x, ChartSpec? y) =>
        ReferenceEquals(x, y)
        || (x is not null
            && y is not null
            && string.Equals(x.Type, y.Type, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Title, y.Title, StringComparison.Ordinal)
            && NullableStringEquals(x.XAxisTitle, y.XAxisTitle)
            && NullableStringEquals(x.YAxisTitle, y.YAxisTitle)
            && SeriesEqual(x.Data, y.Data));

    public int GetHashCode(ChartSpec chart)
    {
        var hash = new HashCode();
        hash.Add(chart.Type, StringComparer.OrdinalIgnoreCase);
        hash.Add(chart.Title, StringComparer.Ordinal);
        hash.Add(chart.XAxisTitle, StringComparer.Ordinal);
        hash.Add(chart.YAxisTitle, StringComparer.Ordinal);
        // Include shape (series + point counts and legends) but not raw doubles,
        // so equal-under-epsilon charts still land in the same bucket.
        hash.Add(chart.Data.Count);
        foreach (ChartSeries series in chart.Data)
        {
            hash.Add(series.Legend, StringComparer.Ordinal);
            hash.Add(series.Values.Count);
        }
        return hash.ToHashCode();
    }

    private static bool SeriesEqual(List<ChartSeries> a, List<ChartSeries> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Legend, b[i].Legend, StringComparison.Ordinal)
                || !NullableStringEquals(a[i].Color, b[i].Color)
                || !PointsEqual(a[i].Values, b[i].Values))
            {
                return false;
            }
        }
        return true;
    }

    private static bool PointsEqual(List<ChartDataPoint> a, List<ChartDataPoint> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].X, b[i].X, StringComparison.Ordinal)
                || Math.Abs(a[i].Y - b[i].Y) > _valueEpsilon)
            {
                return false;
            }
        }
        return true;
    }

    private static bool NullableStringEquals(string? a, string? b) =>
        string.Equals(a, b, StringComparison.Ordinal);
}

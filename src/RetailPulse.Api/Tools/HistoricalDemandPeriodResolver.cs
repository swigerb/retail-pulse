using System.Text.RegularExpressions;

namespace RetailPulse.Api.Tools;

internal readonly record struct HistoricalDemandPeriodResolution(
    string Period,
    bool HasExplicitPeriod,
    bool IsSupported);

internal static partial class HistoricalDemandPeriodResolver
{
    public const string UnsupportedExplicitPeriod = "unsupported-explicit-period";

    public static HistoricalDemandPeriodResolution ResolveUserMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return new("last-12-months", HasExplicitPeriod: false, IsSupported: true);

        if (ContainsAny(message, "this quarter", "current quarter", "quarter to date"))
            return new("current-quarter", HasExplicitPeriod: true, IsSupported: true);

        if (ContainsAny(message, "this month", "current month", "month to date"))
            return new("current-month", HasExplicitPeriod: true, IsSupported: true);

        if (ContainsAny(message, "this year", "current year", "year to date"))
            return new("current-year", HasExplicitPeriod: true, IsSupported: true);

        Match trailingMonths = TrailingMonthsRegex().Match(message);
        int months = 0;
        bool supportedTrailingMonths = trailingMonths.Success
            && int.TryParse(
                    trailingMonths.Groups[1].Value,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out months)
            && months is >= 1 and <= 24;
        return trailingMonths.Success
            ? supportedTrailingMonths
                ? new($"last-{months}-months", HasExplicitPeriod: true, IsSupported: true)
                : new(UnsupportedExplicitPeriod, HasExplicitPeriod: true, IsSupported: false)
            : UnsupportedQuarterRegex().IsMatch(message)
                || ExplicitDateRangeRegex().IsMatch(message)
            ? new(UnsupportedExplicitPeriod, HasExplicitPeriod: true, IsSupported: false)
            : new("last-12-months", HasExplicitPeriod: false, IsSupported: true);
    }

    public static bool IsSupportedToolPeriod(string? period)
    {
        if (string.IsNullOrWhiteSpace(period))
            return true;

        string normalized = period.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        if (normalized is "current-month" or "this-month" or "mtd"
            or "current-quarter" or "this-quarter" or "qtd"
            or "current-year" or "this-year" or "ytd")
        {
            return true;
        }

        Match match = CanonicalTrailingMonthsRegex().Match(normalized);
        return match.Success
            && int.TryParse(match.Groups[1].Value, out int months)
            && months is >= 1 and <= 24;
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"\b(?:last|past)\s*(\d+)\s*months?\b", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingMonthsRegex();

    [GeneratedRegex(@"^last-(\d+)-months$", RegexOptions.IgnoreCase)]
    private static partial Regex CanonicalTrailingMonthsRegex();

    [GeneratedRegex(@"\b(?:last|previous|prior)\s+quarter\b|\bq[1-4]\b|\b(?:first|second|third|fourth)\s+quarter\b", RegexOptions.IgnoreCase)]
    private static partial Regex UnsupportedQuarterRegex();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b|\b(?:from|between)\s+\S+\s+(?:to|and)\s+\S+", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitDateRangeRegex();
}

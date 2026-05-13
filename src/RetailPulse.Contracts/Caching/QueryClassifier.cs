using System.Text.RegularExpressions;

namespace RetailPulse.Contracts.Caching;

/// <summary>
/// Classifies whether a query produces deterministic output suitable for caching.
/// Uses keyword heuristics — no LLM call required.
/// </summary>
public static partial class QueryClassifier
{
    /// <summary>
    /// Returns true when the query + agent combination is expected to produce
    /// repeatable results safe for response caching.
    /// </summary>
    public static bool IsDeterministic(string query, string agentId)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        // Agent-level exclusions: forecasting agent output is inherently non-deterministic
        if (string.Equals(agentId, "demand-forecasting", StringComparison.OrdinalIgnoreCase))
            return false;

        var lower = query.ToLowerInvariant();

        // Never cache: time-sensitive or recommendation-style queries
        if (NeverCachePattern().IsMatch(lower))
            return false;

        // Always cache: factual lookups, definitions, historical data
        if (AlwaysCachePattern().IsMatch(lower))
            return true;

        // Default: cache for General agent, skip for specialists
        return string.Equals(agentId, "general", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(
        @"\b(forecast|predict|recommend|suggest|what should|should i|today|tonight|this week|this month|right now|current|live|real-?time)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex NeverCachePattern();

    [GeneratedRegex(
        @"\b(what is|what are|define|definition|how does|explain|historical|last year|last quarter|last month|fy\d{2,4}|q[1-4]\s?\d{2,4})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex AlwaysCachePattern();
}

namespace RetailPulse.TeamsBot.Cards;

/// <summary>
/// Utility for formatting telemetry data in Adaptive Cards
/// </summary>
public static class TelemetryFormatter
{
    /// <summary>
    /// Formats duration in milliseconds to human-readable format (e.g., "2.3s", "150ms")
    /// </summary>
    public static string FormatDuration(double durationMs)
    {
        if (durationMs >= 1000)
        {
            return $"{durationMs / 1000:F1}s";
        }
        return $"{durationMs:F0}ms";
    }

    /// <summary>
    /// Returns emoji icon based on span type
    /// </summary>
    public static string GetSpanIcon(string spanType)
    {
        return spanType.ToLowerInvariant() switch
        {
            "thought" => "🤔",
            "tool_call" => "🔧",
            "tool_result" => "✅",
            "response" => "📡",
            "foundry" => "⚡",
            "agent_delegation" => "🤖",
            "agent_call" => "🤖",
            _ => "•"
        };
    }

    /// <summary>
    /// Returns type badge text for span type
    /// </summary>
    public static string GetTypeBadge(string spanType)
    {
        return spanType.ToLowerInvariant() switch
        {
            "thought" => "THINK",
            "tool_call" => "TOOL",
            "tool_result" => "RESULT",
            "response" => "REPLY",
            "foundry" => "FOUNDRY",
            "agent_delegation" => "AGENT",
            "agent_call" => "AGENT",
            _ => spanType.ToUpperInvariant()
        };
    }

    /// <summary>
    /// Truncates text to a maximum length with ellipsis
    /// </summary>
    public static string TruncateName(string name, int maxLength = 30)
    {
        if (string.IsNullOrEmpty(name) || name.Length <= maxLength)
        {
            return name ?? string.Empty;
        }

        return name.Substring(0, maxLength - 1) + "…";
    }

    /// <summary>
    /// Truncates detail text, removing newlines
    /// </summary>
    public static string TruncateDetail(string? detail, int maxLength = 60)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return string.Empty;
        }

        // Replace newlines with spaces
        var singleLine = detail.Replace("\r\n", " ").Replace("\n", " ").Trim();

        if (singleLine.Length <= maxLength)
        {
            return singleLine;
        }

        return singleLine.Substring(0, maxLength - 1) + "…";
    }

    /// <summary>
    /// Calculates proportional width percentage for waterfall visualization
    /// </summary>
    public static int CalculateWaterfallWidth(double spanDuration, double maxDuration)
    {
        if (maxDuration <= 0)
        {
            return 0;
        }

        var percentage = (spanDuration / maxDuration) * 100.0;
        return Math.Max(5, (int)Math.Round(percentage)); // Minimum 5% for visibility
    }
}

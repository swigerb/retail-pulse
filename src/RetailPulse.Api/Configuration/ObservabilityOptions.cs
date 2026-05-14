namespace RetailPulse.Api.Configuration;

/// <summary>
/// Quota settings for cost tracker and conversation exporter.
/// </summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>Max cost events retained in memory (default: 10,000).</summary>
    public int MaxCostEvents { get; set; } = 10_000;

    /// <summary>TTL in hours for cost events; older events are evicted on next write (default: 24).</summary>
    public double CostEventTtlHours { get; set; } = 24;

    /// <summary>Max tracked conversation sessions (default: 1,000).</summary>
    public int MaxSessions { get; set; } = 1_000;

    /// <summary>Max messages per session (default: 200).</summary>
    public int MaxMessagesPerSession { get; set; } = 200;
}

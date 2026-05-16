namespace RetailPulse.Api.Caching;

/// <summary>
/// Configuration options for the tool result cache.
/// Bind from "ToolCache" configuration section.
/// </summary>
public class ToolCacheOptions
{
    public const string SectionName = "ToolCache";

    /// <summary>Whether tool result caching is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Default TTL in minutes for tools not explicitly configured.</summary>
    public int DefaultTtlMinutes { get; set; } = 30;

    /// <summary>Per-tool TTL overrides in minutes.</summary>
    public Dictionary<string, int> ToolTtls { get; set; } = new()
    {
        ["GetHistoricalDemand"] = 60,
        ["GetSeasonalityFactors"] = 120,
        ["GenerateForecast"] = 30,
        ["IdentifyDemandRisks"] = 15,
        ["GetShipmentStats"] = 10,
        ["GetFieldSentiment"] = 15,
    };

    public TimeSpan GetTtl(string toolName) =>
        ToolTtls.TryGetValue(toolName, out var minutes)
            ? TimeSpan.FromMinutes(minutes)
            : TimeSpan.FromMinutes(DefaultTtlMinutes);
}

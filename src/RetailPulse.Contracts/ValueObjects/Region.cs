namespace RetailPulse.Contracts.ValueObjects;

/// <summary>
/// Value object representing a geographic region. Validates against known region list.
/// </summary>
public readonly record struct Region
{
    private static readonly HashSet<string> _knownRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Northeast",
        "Southeast",
        "Midwest",
        "Southwest",
        "West Coast",
        "Pacific Northwest",
        "North",
        "South",
        "East",
        "West",
        "Central",
        "National"
    };

    public string Value { get; }

    public Region(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(value));
        var trimmed = value.Trim();
        if (!_knownRegions.Contains(trimmed))
        {
            throw new ArgumentException($"Unknown region: '{trimmed}'. Known regions: {string.Join(", ", _knownRegions)}", nameof(value));
        }
        Value = trimmed;
    }

    /// <summary>
    /// Creates a Region without validation. Use for dynamic/tenant-defined regions.
    /// </summary>
    public static Region FromUnchecked(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(value));
        return new Region(value.Trim(), skipValidation: true);
    }

    private Region(string value, bool skipValidation)
    {
        Value = value;
    }

    public static bool IsKnown(string value) => _knownRegions.Contains(value?.Trim() ?? "");

    public static implicit operator Region(string value) => new(value);
    public static implicit operator string(Region region) => region.Value;

    public override string ToString() => Value;
}

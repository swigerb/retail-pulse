namespace RetailPulse.Contracts.ValueObjects;

/// <summary>
/// Value object representing a brand name. Validates non-empty and provides value equality.
/// </summary>
public readonly record struct BrandName
{
    public string Value { get; }

    public BrandName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(value));
        Value = value.Trim();
    }

    public static implicit operator BrandName(string value) => new(value);
    public static implicit operator string(BrandName brand) => brand.Value;

    public override string ToString() => Value;
}

using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace RetailPulse.Contracts;

/// <summary>
/// Root tenant configuration loaded from tenant.yaml.
/// </summary>
public class TenantConfiguration
{
    public string Company { get; init; } = "";
    public string Industry { get; init; } = "";
    public string Description { get; init; } = "";

    [YamlMember(Alias = "brands")]
    [JsonPropertyName("brands")]
    public List<BrandConfig> BrandsList { get; init; } = new();

    /// <summary>Read-only view of configured brands.</summary>
    [YamlIgnore]
    [JsonIgnore]
    public IReadOnlyList<BrandConfig> Brands => BrandsList;

    [YamlMember(Alias = "regions")]
    [JsonPropertyName("regions")]
    public List<string> RegionsList { get; init; } = new();

    /// <summary>Read-only view of configured regions.</summary>
    [YamlIgnore]
    [JsonIgnore]
    public IReadOnlyList<string> Regions => RegionsList;

    [YamlMember(Alias = "channels")]
    [JsonPropertyName("channels")]
    public List<string> ChannelsList { get; init; } = new();

    /// <summary>Read-only view of configured channels.</summary>
    [YamlIgnore]
    [JsonIgnore]
    public IReadOnlyList<string> Channels => ChannelsList;

    public ThemeConfig Theme { get; init; } = new();
    public DistributionConfig Distribution { get; init; } = new();
}

/// <summary>
/// Configuration for a single brand including its category, variants, and price segment.
/// </summary>
public class BrandConfig
{
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";

    [YamlMember(Alias = "variants")]
    [JsonPropertyName("variants")]
    public List<string> VariantsList { get; init; } = new();

    /// <summary>Read-only view of configured variants.</summary>
    [YamlIgnore]
    [JsonIgnore]
    public IReadOnlyList<string> Variants => VariantsList;

    public string PriceSegment { get; init; } = "";
}

/// <summary>
/// UI theme configuration for the tenant.
/// </summary>
public class ThemeConfig
{
    public string PrimaryColor { get; init; } = "";
    public string AccentColor { get; init; } = "";
    public string LogoPath { get; init; } = "";
    public string FontFamily { get; init; } = "";
}

/// <summary>
/// Distribution model configuration for the tenant.
/// </summary>
public class DistributionConfig
{
    public string Model { get; init; } = "";

    [YamlMember(Alias = "distributorTypes")]
    [JsonPropertyName("distributorTypes")]
    public List<string> DistributorTypesList { get; init; } = new();

    /// <summary>Read-only view of configured distributor types.</summary>
    [YamlIgnore]
    [JsonIgnore]
    public IReadOnlyList<string> DistributorTypes => DistributorTypesList;
}

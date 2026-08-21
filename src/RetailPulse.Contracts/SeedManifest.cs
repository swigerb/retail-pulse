using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace RetailPulse.Contracts;

/// <summary>
/// Scenario-varying seed inputs that a content pack (issue #108) contributes
/// to the MCP server's data seeder. Everything the pack owns declaratively —
/// seasonality tables, competitor rosters, promo coefficients, supply
/// disruption vocabulary, store archetypes, margin driver categories — lives
/// in <c>seed/scenario.yaml</c> so a pack switch changes the SQLite dataset
/// wholesale, not just the tenant metadata.
/// </summary>
/// <remarks>
/// <para>
/// The manifest is intentionally shaped as a small set of logical sections so
/// diagnostic messages can name <c>seed/scenario.yaml#promos</c> or
/// <c>seed/scenario.yaml#competitive</c> when a section is missing or invalid.
/// </para>
/// <para>
/// This type lives in <c>RetailPulse.Contracts</c> because both the API host
/// and the MCP server need to load the same manifest without one referencing
/// the other. Every collection is a mutable <c>List</c> for YamlDotNet
/// deserialization but is exposed through read-only projections downstream.
/// </para>
/// </remarks>
public sealed class SeedManifest
{
    /// <summary>Manifest schema version. Reserved for forward compatibility.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Category → per-month seasonal factors used by demand forecasting
    /// and the seasonality tool.</summary>
    [YamlMember(Alias = "seasonality")]
    [JsonPropertyName("seasonality")]
    public SeasonalitySection Seasonality { get; init; } = new();

    /// <summary>Competitor rosters and competitive intelligence templates.</summary>
    [YamlMember(Alias = "competitive")]
    [JsonPropertyName("competitive")]
    public CompetitiveSection Competitive { get; init; } = new();

    /// <summary>Promo types + lift coefficient bands and rating labels.</summary>
    [YamlMember(Alias = "promos")]
    [JsonPropertyName("promos")]
    public PromosSection Promos { get; init; } = new();

    /// <summary>Supply-chain disruption vocabulary.</summary>
    [YamlMember(Alias = "supply")]
    [JsonPropertyName("supply")]
    public SupplySection Supply { get; init; } = new();

    /// <summary>Store operations vocabulary.</summary>
    [YamlMember(Alias = "stores")]
    [JsonPropertyName("stores")]
    public StoresSection Stores { get; init; } = new();

    /// <summary>Margin analytics vocabulary.</summary>
    [YamlMember(Alias = "margin")]
    [JsonPropertyName("margin")]
    public MarginSection Margin { get; init; } = new();
}

/// <summary>Seasonality section — a category → month-factor list.</summary>
public sealed class SeasonalitySection
{
    [YamlMember(Alias = "factors")]
    [JsonPropertyName("factors")]
    public Dictionary<string, List<SeasonalMonthFactor>> FactorsMap { get; init; } = [];

    [YamlIgnore]
    [JsonIgnore]
    public IReadOnlyDictionary<string, IReadOnlyList<SeasonalMonthFactor>> Factors =>
        FactorsMap.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<SeasonalMonthFactor>)kv.Value,
            StringComparer.OrdinalIgnoreCase);
}

/// <summary>A single (category, month) seasonal factor.</summary>
public sealed class SeasonalMonthFactor
{
    public int Month { get; init; }
    public double Multiplier { get; init; } = 1.0;
    public string Event { get; init; } = "";
    public string Description { get; init; } = "";
}

public sealed class CompetitiveSection
{
    [YamlMember(Alias = "competitorsByCategory")]
    [JsonPropertyName("competitorsByCategory")]
    public Dictionary<string, List<string>> CompetitorsByCategoryMap { get; init; } = [];

    [YamlIgnore]
    [JsonIgnore]
    public IReadOnlyDictionary<string, IReadOnlyList<string>> CompetitorsByCategory =>
        CompetitorsByCategoryMap.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value,
            StringComparer.OrdinalIgnoreCase);

    [YamlMember(Alias = "pricingSources")]
    [JsonPropertyName("pricingSources")]
    public List<string> PricingSourcesList { get; init; } = [];

    [YamlIgnore]
    [JsonIgnore]
    public IReadOnlyList<string> PricingSources => PricingSourcesList;

    [YamlMember(Alias = "shareSources")]
    [JsonPropertyName("shareSources")]
    public List<string> ShareSourcesList { get; init; } = [];

    [YamlIgnore]
    [JsonIgnore]
    public IReadOnlyList<string> ShareSources => ShareSourcesList;

    [YamlMember(Alias = "activityTypes")]
    [JsonPropertyName("activityTypes")]
    public List<string> ActivityTypesList { get; init; } = [];

    [YamlIgnore]
    [JsonIgnore]
    public IReadOnlyList<string> ActivityTypes => ActivityTypesList;

    [YamlMember(Alias = "impactLevels")]
    [JsonPropertyName("impactLevels")]
    public List<string> ImpactLevelsList { get; init; } = [];

    [YamlIgnore]
    [JsonIgnore]
    public IReadOnlyList<string> ImpactLevels => ImpactLevelsList;

    [YamlMember(Alias = "activityTemplates")]
    [JsonPropertyName("activityTemplates")]
    public List<ActivityTemplate> ActivityTemplatesList { get; init; } = [];

    [YamlIgnore]
    [JsonIgnore]
    public IReadOnlyList<ActivityTemplate> ActivityTemplates => ActivityTemplatesList;
}

public sealed class ActivityTemplate
{
    public string Type { get; init; } = "";
    public string Description { get; init; } = "";
    public string Recommendation { get; init; } = "";
}

public sealed class PromosSection
{
    [YamlMember(Alias = "types")]
    [JsonPropertyName("types")]
    public List<PromoTypeConfig> TypesList { get; init; } = [];

    [YamlIgnore]
    [JsonIgnore]
    public IReadOnlyList<PromoTypeConfig> Types => TypesList;

    [YamlMember(Alias = "successRatings")]
    [JsonPropertyName("successRatings")]
    public List<string> SuccessRatingsList { get; init; } = [];

    [YamlIgnore]
    [JsonIgnore]
    public IReadOnlyList<string> SuccessRatings => SuccessRatingsList;
}

/// <summary>
/// Per-promo-type coefficient band used by both the historic promo campaign
/// seed (SeedPromoHistory) and the aggregated LiftCoefficients table
/// (SeedLiftCoefficients). Keeping both bands on the same object lets a pack
/// author move both together for a single promo type in one place.
/// </summary>
public sealed class PromoTypeConfig
{
    public string Name { get; init; } = "";

    /// <summary>Historic-campaign lift base — used in the per-campaign
    /// <c>SeedPromoHistory</c> seeder.</summary>
    public double LiftBase { get; init; }
    public double LiftRange { get; init; }

    /// <summary>Aggregated coefficient band base — used in the
    /// <c>SeedLiftCoefficients</c> seeder.</summary>
    public double CoefBase { get; init; }
    public double CoefRange { get; init; }

    /// <summary>Optional short code exposed by <c>GET /api/promo/types</c>.
    /// Defaults to a lowercased slug of <see cref="Name"/> when absent.</summary>
    public string? Code { get; init; }

    /// <summary>Optional display name shown by <c>GET /api/promo/types</c>.
    /// Defaults to <see cref="Name"/> when absent.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Optional description shown by <c>GET /api/promo/types</c>.</summary>
    public string? Description { get; init; }
}

public sealed class SupplySection
{
    [YamlMember(Alias = "disruptionTypes")]
    [JsonPropertyName("disruptionTypes")]
    public List<string> DisruptionTypesList { get; init; } = [];

    [YamlIgnore]
    [JsonIgnore]
    public IReadOnlyList<string> DisruptionTypes => DisruptionTypesList;

    [YamlMember(Alias = "disruptionSeverities")]
    [JsonPropertyName("disruptionSeverities")]
    public List<string> DisruptionSeveritiesList { get; init; } = [];

    [YamlIgnore]
    [JsonIgnore]
    public IReadOnlyList<string> DisruptionSeverities => DisruptionSeveritiesList;

    [YamlMember(Alias = "disruptionDescriptions")]
    [JsonPropertyName("disruptionDescriptions")]
    public Dictionary<string, List<string>> DisruptionDescriptionsMap { get; init; } = [];

    [YamlIgnore]
    [JsonIgnore]
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DisruptionDescriptions =>
        DisruptionDescriptionsMap.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value,
            StringComparer.OrdinalIgnoreCase);
}

public sealed class StoresSection
{
    [YamlMember(Alias = "types")]
    [JsonPropertyName("types")]
    public List<string> TypesList { get; init; } = [];

    [YamlIgnore]
    [JsonIgnore]
    public IReadOnlyList<string> Types => TypesList;
}

public sealed class MarginSection
{
    [YamlMember(Alias = "driverCategories")]
    [JsonPropertyName("driverCategories")]
    public List<string> DriverCategoriesList { get; init; } = [];

    [YamlIgnore]
    [JsonIgnore]
    public IReadOnlyList<string> DriverCategories => DriverCategoriesList;

    [YamlMember(Alias = "trendLabels")]
    [JsonPropertyName("trendLabels")]
    public List<string> TrendLabelsList { get; init; } = [];

    [YamlIgnore]
    [JsonIgnore]
    public IReadOnlyList<string> TrendLabels => TrendLabelsList;
}

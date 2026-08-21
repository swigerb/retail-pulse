using YamlDotNet.Serialization;

namespace RetailPulse.Api.Packs;

/// <summary>
/// Metadata block declared at the top of a pack's <c>pack.yaml</c>.
/// Identifies the pack and provides operator-facing display fields so a
/// packs directory can be listed and picked from configuration without
/// reading every section.
/// </summary>
public sealed class PackMetadata
{
    /// <summary>
    /// Stable identifier used by <c>Packs:Active</c> configuration to
    /// select this pack. Lowercase kebab-case by convention and MUST
    /// match the pack's directory name so operators can find the source
    /// on disk from the configured active key.
    /// </summary>
    public string Key { get; init; } = "";

    /// <summary>Human-readable label shown in operator-facing surfaces.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Free-form description of the retail scenario this pack represents.</summary>
    public string Description { get; init; } = "";

    /// <summary>Semver-shaped version string. Foundation is informational only.</summary>
    public string Version { get; init; } = "";

    /// <summary>
    /// Retail segment label — for example "Multi-Category Retail" or
    /// "Pet Supply". Complements <see cref="TenantConfiguration.Industry"/>
    /// which is projected into agent prompts; this stays with pack
    /// metadata for catalog surfaces.
    /// </summary>
    public string Segment { get; init; } = "";

    /// <summary>
    /// Optional attribution note surfaced next to the pack in the catalog.
    /// Sample packs use this to advertise their fictional status so
    /// operators never mistake sample data for real production content.
    /// </summary>
    [YamlMember(Alias = "attribution")]
    public string Attribution { get; init; } = "";
}

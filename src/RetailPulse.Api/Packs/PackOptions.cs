namespace RetailPulse.Api.Packs;

/// <summary>
/// Configuration binding for content-pack selection. A downstream host
/// reads <see cref="Active"/> and <see cref="Root"/> from the
/// <c>Packs</c> section of appsettings to pick which pack the platform
/// runs. Foundation ships the <c>default</c> pack that reproduces the
/// pre-#108 sample tenant byte-for-byte, so an unconfigured deployment
/// keeps its existing behaviour.
/// </summary>
public sealed class PackOptions
{
    public const string SectionName = "Packs";

    /// <summary>
    /// Directory name of the pack to load. Defaults to <c>default</c>
    /// so a fresh clone with no configuration overrides picks up the
    /// sample tenant. Case-insensitive against the on-disk directory
    /// name so a Windows/Linux path drift can't silently miss a pack.
    /// </summary>
    public string Active { get; set; } = "default";

    /// <summary>
    /// Root directory containing the pack folders. Relative paths are
    /// resolved against the composition root's content root. Defaults
    /// to <c>packs</c> at the repo root.
    /// </summary>
    public string Root { get; set; } = "packs";
}

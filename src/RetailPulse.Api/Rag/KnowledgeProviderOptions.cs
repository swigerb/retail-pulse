namespace RetailPulse.Api.Rag;

/// <summary>
/// Configuration surface for selecting the active knowledge provider and its
/// degradation policy. Bound to the <c>Knowledge:Provider</c> section.
///
/// A missing or empty section is a documented, deliberate default:
/// <see cref="Mode"/> resolves to <see cref="KnowledgeProviderMode.InMemory"/>
/// and <see cref="Degradation"/> to <see cref="KnowledgeDegradationMode.FailLoud"/>.
/// The default keeps the platform runnable on a laptop with no cloud resources.
///
/// An unknown or malformed value in either field fails startup with a clear,
/// actionable message via <see cref="KnowledgeProviderSelector"/>. This class
/// intentionally does not attempt to auto-detect a provider from ambient
/// signals — the choice is always explicit configuration.
/// </summary>
public sealed class KnowledgeProviderOptions
{
    /// <summary>Configuration section: <c>Knowledge:Provider</c>.</summary>
    public const string SectionName = "Knowledge:Provider";

    /// <summary>Full configuration key for the mode selector.</summary>
    public const string ModeKey = "Knowledge:Provider:Mode";

    /// <summary>Full configuration key for the degradation policy.</summary>
    public const string DegradationKey = "Knowledge:Provider:Degradation";

    /// <summary>
    /// Raw mode value from configuration. Blank means "use the default". Kept
    /// as a string so the selector can produce actionable error messages that
    /// echo exactly what the operator wrote.
    /// </summary>
    public string? Mode { get; set; }

    /// <summary>
    /// Raw degradation value from configuration. Blank means "use the default"
    /// (fail loud).
    /// </summary>
    public string? Degradation { get; set; }
}

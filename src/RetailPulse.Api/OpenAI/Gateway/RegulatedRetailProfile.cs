using System.Diagnostics.CodeAnalysis;

namespace RetailPulse.Api.OpenAI.Gateway;

/// <summary>
/// Regulated-retail gateway mode (#337) for the synthetic pharmacy scenario. This is a configuration
/// posture (body-free diagnostics, fail-closed safety, sensitive cache bypass, masked identifiers).
/// It is NOT a HIPAA certification or attestation, and patient data / clinical advice are forbidden.
/// </summary>
public sealed record RegulatedRetailProfile(
    bool Enabled,
    string Mode,
    string Label,
    string Disclaimer,
    bool CaptureMessageBodies,
    bool SafetyFailClosed,
    IReadOnlyList<string> CacheBypassClasses)
{
    public const string ConfigKey = "Gateway:RegulatedRetail:Enabled";
    public const string DemoLabel = "Synthetic demo mode";
    public const string RegulatedLabel = "Regulated-retail mode (synthetic data only, not HIPAA certified)";
    public const string DisclaimerText = "Configuration posture for synthetic demonstrations. Not HIPAA certified; do not enter patient data or request clinical advice.";

    public static RegulatedRetailProfile Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        bool enabled = bool.TryParse(configuration[ConfigKey]?.Trim(), out bool v) && v;
        return enabled
            ? new RegulatedRetailProfile(true, "regulated-retail", RegulatedLabel, DisclaimerText, false, true, SensitiveCachePolicy.BypassClasses)
            : new RegulatedRetailProfile(false, "synthetic-demo", DemoLabel, DisclaimerText, true, false, []);
    }
}

/// <summary>Classifications that must never be served from or stored in a semantic cache.</summary>
public static class SensitiveCachePolicy
{
    public static IReadOnlyList<string> BypassClasses { get; } =
        ["pharmacy", "recall", "identity", "patient", "clinical", "health", "sensitive"];

    /// <summary>Unknown/blank classification fails safe: regulated mode bypasses the cache.</summary>
    public static bool ShouldBypass(bool regulated, string? classification)
    {
        if (!regulated)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(classification))
        {
            return true;
        }

        string c = classification.Trim();
        return BypassClasses.Any(b => c.Contains(b, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Masks correlation and caller identifiers before they are shown to clients.</summary>
public static class ClientIdentifierMasker
{
    [return: NotNullIfNotNull(nameof(value))]
    public static string? Mask(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= 8 ? new string('*', value.Length) : value[..4] + "…" + value[^2..];
    }
}

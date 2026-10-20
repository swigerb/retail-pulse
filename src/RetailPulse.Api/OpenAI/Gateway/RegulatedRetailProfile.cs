using System.Diagnostics.CodeAnalysis;
using RetailPulse.Contracts.Guardrails;

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

    public static RegulatedRetailProfile Resolve(IConfiguration configuration) =>
        Resolve(configuration, new ContentSafetyConfig());

    /// <summary>SafetyFailClosed is derived from the effective Content Safety fail policy.</summary>
    public static RegulatedRetailProfile Resolve(IConfiguration configuration, ContentSafetyConfig contentSafety)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        bool enabled = bool.TryParse(configuration[ConfigKey]?.Trim(), out bool v) && v;
        bool failClosed = contentSafety.Enabled
            && contentSafety.OnUnavailable == ContentSafetyFailPolicy.FailClosed;
        return enabled
            ? new RegulatedRetailProfile(true, "regulated-retail", RegulatedLabel, DisclaimerText, false, failClosed, SensitiveCachePolicy.BypassClasses)
            : new RegulatedRetailProfile(false, "synthetic-demo", DemoLabel, DisclaimerText, true, failClosed, []);
    }
}

/// <summary>Classifications that must never be served from or stored in a semantic cache.</summary>
public static class SensitiveCachePolicy
{
    public static IReadOnlyList<string> BypassClasses { get; } =
        ["pharmacy", "recall", "identity", "patient", "clinical", "health", "sensitive"];

    /// <summary>
    /// The chat cache has no request classifier, so regulated mode cannot prove any input is non-sensitive
    /// (pharmacy, recall, identity, patient, clinical, health, blank/unknown, mutation) and bypasses it entirely.
    /// </summary>
    public static bool ShouldBypassForRequest(bool regulated, string? message) => regulated;

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

        // ASCII-only output: Kestrel rejects non-Latin-1/control characters in header values.
        static char Safe(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '*';

        string safe = string.Concat(value.Select(Safe));
        return safe.Length <= 8 ? new string('*', safe.Length) : safe[..4] + "..." + safe[^2..];
    }
}

namespace RetailPulse.Contracts.Guardrails;

/// <summary>
/// Runtime-configurable guardrails settings exposed via the config endpoints.
/// </summary>
public class GuardrailsConfig
{
    /// <summary>Whether PII detection is active.</summary>
    public bool PiiDetectionEnabled { get; set; } = true;

    /// <summary>Whether jailbreak detection is active.</summary>
    public bool JailbreakDetectionEnabled { get; set; } = true;

    /// <summary>Whether detected PII is automatically redacted vs. blocked outright.</summary>
    public bool AutoRedactPii { get; set; } = true;

    /// <summary>Maximum input length in characters before the request is rejected.</summary>
    public int MaxInputLength { get; set; } = 10_000;

    /// <summary>Patterns currently loaded for PII detection.</summary>
    public IReadOnlyList<string> PiiPatterns { get; set; } = [];

    /// <summary>Patterns currently loaded for jailbreak detection.</summary>
    public IReadOnlyList<string> JailbreakPatterns { get; set; } = [];

    /// <summary>
    /// Optional Azure AI Content Safety second layer configuration. Disabled by
    /// default — when disabled the layer is an in-process no-op and behavior is
    /// byte-for-byte equal to the pattern-only guardrails.
    /// </summary>
    public ContentSafetyConfig ContentSafety { get; set; } = new();
}

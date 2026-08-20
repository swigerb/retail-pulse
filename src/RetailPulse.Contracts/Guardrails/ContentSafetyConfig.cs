namespace RetailPulse.Contracts.Guardrails;

/// <summary>
/// Runtime configuration for the optional Azure AI Content Safety second layer
/// that runs behind the local pattern guardrails. Disabled by default so the
/// service builds, starts, and passes its tests with no Content Safety resource
/// configured and the observable behavior is byte-for-byte identical to the
/// regex-only guardrails path.
/// </summary>
/// <remarks>
/// Authentication is managed identity only — there is deliberately no
/// <c>ApiKey</c> / <c>Key</c> / <c>SecretKey</c> property on this type so a key
/// cannot be smuggled through configuration. The
/// <c>NoKeyOnContentSafetyConfig</c> contract test enforces the absence of any
/// such member via reflection.
/// </remarks>
public class ContentSafetyConfig
{
    /// <summary>Master switch. When <c>false</c>, every Content Safety stage is a no-op.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Content Safety account endpoint (e.g. <c>https://&lt;acs&gt;.cognitiveservices.azure.com</c>).
    /// Read server-side only — never returned by <c>/api/guardrails/config</c>.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>Fail policy when the remote layer is unavailable (breaker open, timeout, 5xx).</summary>
    public ContentSafetyFailPolicy OnUnavailable { get; set; } = ContentSafetyFailPolicy.FailOpen;

    /// <summary>Bounded per-call timeout in milliseconds for a single Content Safety call.</summary>
    public int TimeoutMs { get; set; } = 1500;

    /// <summary>When <c>true</c>, evaluates user input.</summary>
    public bool CheckInput { get; set; } = true;

    /// <summary>When <c>true</c>, evaluates model output.</summary>
    public bool CheckOutput { get; set; } = true;

    /// <summary>When <c>true</c>, evaluates retrieved knowledge chunks before injection.</summary>
    public bool CheckRetrievedKnowledge { get; set; } = true;

    /// <summary>When <c>true</c>, evaluates tool results before they reach the model.</summary>
    public bool CheckToolResults { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, runs Prompt Shields for user input and indirect-injection
    /// checks against retrieved knowledge chunks.
    /// </summary>
    public bool PromptShieldsEnabled { get; set; } = true;

    /// <summary>Per-category severity thresholds for the text moderation layer.</summary>
    public ContentSafetyCategoryThresholds Thresholds { get; set; } = new();
}

/// <summary>Policy applied when the Content Safety service is unavailable.</summary>
public enum ContentSafetyFailPolicy
{
    /// <summary>Pass through with an audit row; regulated deployments should NOT use this.</summary>
    FailOpen = 0,

    /// <summary>Refuse the request with a distinct refusal message and an audit row.</summary>
    FailClosed = 1,
}

/// <summary>
/// Per-category severity thresholds. A category hit blocks the request when its
/// severity is greater than or equal to the configured threshold. The Content
/// Safety severity axis is 0, 2, 4, 6 (per the service contract); a threshold
/// of 4 corresponds to the default "medium" cutoff.
/// </summary>
public class ContentSafetyCategoryThresholds
{
    /// <summary>Threshold for the Hate category.</summary>
    public int Hate { get; set; } = 4;

    /// <summary>Threshold for the Sexual category.</summary>
    public int Sexual { get; set; } = 4;

    /// <summary>Threshold for the Violence category.</summary>
    public int Violence { get; set; } = 4;

    /// <summary>Threshold for the SelfHarm category.</summary>
    public int SelfHarm { get; set; } = 4;

    /// <summary>Resolves the configured threshold for a case-insensitive category name.</summary>
    public int Resolve(string category)
    {
        return string.Equals(category, "Hate", StringComparison.OrdinalIgnoreCase) ? Hate
            : string.Equals(category, "Sexual", StringComparison.OrdinalIgnoreCase) ? Sexual
            : string.Equals(category, "Violence", StringComparison.OrdinalIgnoreCase) ? Violence
            : string.Equals(category, "SelfHarm", StringComparison.OrdinalIgnoreCase) ? SelfHarm
            : int.MaxValue;
    }
}

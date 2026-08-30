namespace RetailPulse.Api.Guardrails.ContentSafety;

/// <summary>
/// Well-known <see cref="Contracts.Guardrails.SuspiciousRequest.DetectionType"/>
/// values emitted by the Content Safety layer. Every value is treated as a
/// content-safety block by the in-memory log so the dashboard counters and
/// filter chips can be additive without another audit-schema change.
/// </summary>
public static class ContentSafetyDetectionTypes
{
    public const string Prefix = "content-safety-";

    public const string Hate = "content-safety-hate";
    public const string Sexual = "content-safety-sexual";
    public const string Violence = "content-safety-violence";
    public const string SelfHarm = "content-safety-selfharm";
    public const string PromptShield = "content-safety-prompt-shield";
    public const string IndirectInjection = "content-safety-indirect-injection";
    public const string Unavailable = "content-safety-unavailable";

    /// <summary>
    /// Generic Content Safety block used when the evaluator returns a
    /// <see cref="ContentSafetyDecision.Blocked"/> decision without a
    /// resolvable category — for example, an output-side block where no
    /// primary category made it into the response. Never labeled as a
    /// prompt-shield detection because the output stage does not run Prompt
    /// Shields.
    /// </summary>
    public const string Block = "content-safety-block";

    /// <summary>Returns <c>true</c> when the detection type belongs to the Content Safety layer.</summary>
    public static bool IsContentSafety(string? detectionType) =>
        !string.IsNullOrEmpty(detectionType)
            && detectionType.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Maps a Content Safety category name to its detection-type constant.</summary>
    public static string ForCategory(string category)
    {
        return string.Equals(category, "Hate", StringComparison.OrdinalIgnoreCase) ? Hate
            : string.Equals(category, "Sexual", StringComparison.OrdinalIgnoreCase) ? Sexual
            : string.Equals(category, "Violence", StringComparison.OrdinalIgnoreCase) ? Violence
            : string.Equals(category, "SelfHarm", StringComparison.OrdinalIgnoreCase) ? SelfHarm
            : $"{Prefix}{category.ToLowerInvariant()}";
    }

    /// <summary>
    /// Picks the most specific detection type for an evaluator result on a
    /// stage that <em>does not</em> run Prompt Shields (Output). Falls back to
    /// <see cref="Block"/> when no category information is available so an
    /// output-only block is never mislabeled as a prompt shield hit.
    /// </summary>
    public static string ForResultWithoutShield(ContentSafetyResult result)
    {
        return result.PrimaryCategory is { Length: > 0 } primary
            ? primary
            : result.Categories.Count > 0
                ? ForCategory(result.Categories[0].Category)
                : Block;
    }

    /// <summary>
    /// Picks the most specific detection type for an evaluator result on a
    /// stage that runs Prompt Shields (Input, RetrievedKnowledge, ToolResult).
    /// Prompt Shields detections take precedence, then category, then a generic
    /// <see cref="Block"/>.
    /// </summary>
    public static string ForResultWithShield(ContentSafetyResult result, bool preferIndirect = false)
    {
        if (preferIndirect)
        {
            if (result.PromptShieldIndirectInjectionDetected) return IndirectInjection;
            if (result.PromptShieldJailbreakDetected) return PromptShield;
        }
        else
        {
            if (result.PromptShieldJailbreakDetected) return PromptShield;
            if (result.PromptShieldIndirectInjectionDetected) return IndirectInjection;
        }
        return ForResultWithoutShield(result);
    }
}

/// <summary>
/// Well-known <see cref="Contracts.Guardrails.SuspiciousRequest.Action"/>
/// values used by the Content Safety layer to make fail-open vs fail-closed
/// behavior distinguishable in the audit trail.
/// </summary>
public static class ContentSafetyActions
{
    public const string Blocked = "blocked";
    public const string Dropped = "dropped";
    public const string Flagged = "flagged";
    public const string FailOpenPassed = "failopen-passed";
    public const string FailClosedBlocked = "failclosed-blocked";
}

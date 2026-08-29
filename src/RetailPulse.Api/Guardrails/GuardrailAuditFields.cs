using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Api.Guardrails;

/// <summary>
/// The single authority for the human-readable <see cref="SuspiciousRequest.Reason"/>
/// written to the guardrails audit log, across every detection family.
///
/// The reason is authored here, on the server, because this is the only layer
/// that holds the evaluation, the configured threshold, and the stage at once.
/// The dashboard renders the resulting string verbatim. Nothing downstream may
/// re-derive it: a second author drifts from this one and there is no test that
/// would catch the divergence.
///
/// Everything produced here is constructed from enums, configured thresholds,
/// and detection-type constants. Request payload text is never interpolated in.
/// </summary>
internal static class GuardrailAuditFields
{
    public static (string? Category, int? Severity) PickCategoryAndSeverity(ContentSafetyResult evaluation)
    {
        if (evaluation.Categories.Count == 0) return (null, null);

        string? primaryCategory = CategoryFromDetectionType(evaluation.PrimaryCategory);
        if (primaryCategory is not null)
        {
            ContentSafetyCategoryHit? primaryHit = evaluation.Categories.FirstOrDefault(h =>
                string.Equals(h.Category, primaryCategory, StringComparison.OrdinalIgnoreCase));
            if (primaryHit is not null)
            {
                return (primaryHit.Category, primaryHit.Severity);
            }
        }

        ContentSafetyCategoryHit top = evaluation.Categories[0];
        for (int i = 1; i < evaluation.Categories.Count; i++)
        {
            if (evaluation.Categories[i].Severity > top.Severity)
                top = evaluation.Categories[i];
        }

        return (top.Category, top.Severity);
    }

    public static int? ThresholdFor(ContentSafetyConfig config, string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return null;

        int threshold = config.Thresholds.Resolve(category);
        return threshold == int.MaxValue ? null : threshold;
    }

    public static string BuildReason(
        ContentSafetyResult evaluation,
        ContentSafetyStage stage,
        string detectionType,
        string? category,
        int? severity,
        int? threshold)
    {
        string target = FormatStageTarget(stage);

        if (evaluation.Decision == ContentSafetyDecision.ServiceUnavailable
            || string.Equals(detectionType, ContentSafetyDetectionTypes.Unavailable, StringComparison.Ordinal))
        {
            return $"Content Safety was unreachable while checking {target}.";
        }

        if (string.Equals(detectionType, ContentSafetyDetectionTypes.PromptShield, StringComparison.Ordinal))
        {
            return $"Prompt Shields detected an instruction override attempt in {target}.";
        }

        if (string.Equals(detectionType, ContentSafetyDetectionTypes.IndirectInjection, StringComparison.Ordinal))
        {
            return $"Prompt Shields detected an indirect injection attempt in {target}.";
        }

        string categoryText = string.IsNullOrWhiteSpace(category)
            ? "a configured category"
            : $"{category} content";

        if (severity.HasValue && threshold.HasValue)
        {
            string comparison = severity.Value >= threshold.Value ? "met" : "did not meet";
            return $"Content Safety classified {target} as {categoryText} at severity {severity.Value}, which {comparison} threshold {threshold.Value}.";
        }

        return severity.HasValue
            ? $"Content Safety classified {target} as {categoryText} at severity {severity.Value}."
            : $"Content Safety classified {target} as {categoryText}.";
    }

    /// <summary>
    /// Reason for the pattern-matching layer, which runs before Content Safety
    /// and has no severity axis or threshold to report.
    /// </summary>
    public static string BuildPatternReason(string detectionType, ContentSafetyStage stage)
    {
        string target = FormatStageTarget(stage);
        return detectionType switch
        {
            PatternDetectionTypes.Jailbreak =>
                $"Pattern matching found a known jailbreak phrase in {target}.",
            PatternDetectionTypes.Injection =>
                $"Pattern matching found a known SQL or script injection payload in {target}.",
            PatternDetectionTypes.Pii =>
                $"Pattern matching found personal information in {target}.",
            _ => $"A configured pattern rule matched {target}.",
        };
    }

    /// <summary>
    /// Reason for the output PII sweep. The count is the operator-actionable
    /// part, so it is reported rather than left to be inferred from the preview.
    /// </summary>
    public static string BuildPiiRedactionReason(int redactionCount)
    {
        string items = redactionCount == 1 ? "1 value" : $"{redactionCount} values";
        return $"Pattern matching found {items} matching a personal-information rule in the output.";
    }

    private static string FormatStageTarget(ContentSafetyStage stage) => stage switch
    {
        ContentSafetyStage.Input => "the input",
        ContentSafetyStage.Output => "the output",
        ContentSafetyStage.RetrievedKnowledge => "retrieved knowledge",
        ContentSafetyStage.ToolResult => "the tool result",
        ContentSafetyStage.AgentDefinition => "the agent definition",
        _ => "the content",
    };

    private static string? CategoryFromDetectionType(string? detectionType) => detectionType switch
    {
        ContentSafetyDetectionTypes.Hate => "Hate",
        ContentSafetyDetectionTypes.Sexual => "Sexual",
        ContentSafetyDetectionTypes.Violence => "Violence",
        ContentSafetyDetectionTypes.SelfHarm => "SelfHarm",
        _ => null,
    };
}

/// <summary>
/// Detection-type identifiers for the pattern-matching guardrail layer. These
/// were previously bare string literals at each call site, which is how
/// <c>"injection"</c> came to be emitted by the API without ever being added to
/// the frontend's detection-type union.
/// </summary>
public static class PatternDetectionTypes
{
    public const string Jailbreak = "jailbreak";
    public const string Injection = "injection";
    public const string Pii = "pii";

    public const string ActionBlocked = "blocked";
    public const string ActionRedacted = "redacted";
}

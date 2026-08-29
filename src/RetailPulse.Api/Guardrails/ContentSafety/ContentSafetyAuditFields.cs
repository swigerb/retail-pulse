using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Api.Guardrails.ContentSafety;

internal static class ContentSafetyAuditFields
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

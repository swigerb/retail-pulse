using System.Text.Json;
using System.Text.Json.Nodes;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Api.Guardrails.ContentSafety;

/// <summary>
/// Non-Agents seam for tool-result content-safety inspection. Invoked from
/// <see cref="Budget.BudgetedAIFunction"/> (which is registered outside
/// <c>src/RetailPulse.Api/Agents/**</c>) via the
/// <see cref="ContentSafetyToolResultAmbient"/> ambient accessor so no per-agent
/// change is required. When Content Safety is disabled, the caller resolves the
/// no-op evaluator and this inspector returns the original result verbatim.
/// </summary>
/// <remarks>
/// A blocked tool result is replaced with a compact JSON diagnostic that
/// signals the block to the model without leaking the flagged payload. A
/// <see cref="SuspiciousRequest"/> row is emitted for every block, matching the
/// audit contract for the other Content Safety stages.
/// </remarks>
public sealed class ContentSafetyToolResultInspector
{
    private readonly IContentSafetyEvaluator _evaluator;
    private readonly ISuspiciousRequestLog _log;
    private readonly GuardrailsConfig _config;
    private readonly ILogger<ContentSafetyToolResultInspector> _logger;

    public ContentSafetyToolResultInspector(
        IContentSafetyEvaluator evaluator,
        ISuspiciousRequestLog log,
        GuardrailsConfig config,
        ILogger<ContentSafetyToolResultInspector> logger)
    {
        _evaluator = evaluator;
        _log = log;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Inspects a serialized tool result. Returns the (possibly-substituted)
    /// payload the caller should surface to the model.
    /// </summary>
    public async Task<ContentSafetyToolResultOutcome> InspectAsync(
        string toolName,
        string toolResultJson,
        string userId,
        CancellationToken cancellationToken)
    {
        ContentSafetyConfig cfg = _config.ContentSafety;
        if (!cfg.Enabled || !cfg.CheckToolResults || string.IsNullOrWhiteSpace(toolResultJson))
        {
            return ContentSafetyToolResultOutcome.PassThrough(toolResultJson);
        }

        // Structured output is rendered as prose before scanning. This changes
        // how the payload is presented to the classifier, never which checks it
        // faces: the full all-categories harm scan still applies to every tool
        // result, and no tool is trusted into a weaker policy. See #248.
        string scanText = ToolResultTextNormalizer.Normalize(toolResultJson);

        var ctx = new ContentSafetyEvaluationContext(
            UserId: userId,
            SourceId: toolName,
            // A tool result is data the model is about to read, so the threat it
            // carries is a document instructing the model, not a user jailbreak.
            // The evaluator submits this stage to Prompt Shields as a document
            // so indirect-injection detection is the one that fires.
            CheckPromptShield: true);

        ContentSafetyResult evaluation = await _evaluator.EvaluateAsync(
            scanText,
            ContentSafetyStage.ToolResult,
            ctx,
            cancellationToken).ConfigureAwait(false);

        switch (evaluation.Decision)
        {
            case ContentSafetyDecision.Blocked:
                {
                    string detectionType = ContentSafetyDetectionTypes.ForResultWithShield(
                        evaluation,
                        preferIndirect: true);
                    (string? category, int? severity) = GuardrailAuditFields.PickCategoryAndSeverity(evaluation);
                    int? threshold = GuardrailAuditFields.ThresholdFor(cfg, category);

                    await _log.LogAsync(new SuspiciousRequest(
                        Guid.NewGuid().ToString("N"),
                        DateTime.UtcNow,
                        $"Tool result from '{toolName}' blocked by Content Safety",
                        detectionType,
                        userId,
                        ContentSafetyActions.Blocked,
                        Category: category,
                        Severity: severity,
                        Decision: evaluation.Decision.ToString(),
                        Stage: ContentSafetyStage.ToolResult.ToString(),
                        Threshold: threshold,
                        Reason: GuardrailAuditFields.BuildReason(
                            evaluation,
                            ContentSafetyStage.ToolResult,
                            detectionType,
                            category,
                            severity,
                            threshold),
                        Subject: $"Tool result from '{toolName}'"), cancellationToken).ConfigureAwait(false);

                    _logger.LogWarning(
                        "Content Safety blocked tool result from '{Tool}' (decision={Decision}, categories={CategoryCount})",
                        toolName, evaluation.Decision, evaluation.Categories.Count);

                    string substitute = BuildBlockedSubstitute(toolName, detectionType, category, severity);
                    return ContentSafetyToolResultOutcome.Blocked(substitute, detectionType);
                }
            case ContentSafetyDecision.ServiceUnavailable:
                {
                    string action = cfg.OnUnavailable == ContentSafetyFailPolicy.FailClosed
                        ? ContentSafetyActions.FailClosedBlocked
                        : ContentSafetyActions.FailOpenPassed;

                    await _log.LogAsync(new SuspiciousRequest(
                        Guid.NewGuid().ToString("N"),
                        DateTime.UtcNow,
                        $"Content Safety unavailable while checking tool result from '{toolName}'",
                        ContentSafetyDetectionTypes.Unavailable,
                        userId,
                        action,
                        Category: null,
                        Severity: null,
                        Decision: evaluation.Decision.ToString(),
                        Stage: ContentSafetyStage.ToolResult.ToString(),
                        Threshold: null,
                        Reason: GuardrailAuditFields.BuildReason(
                            evaluation,
                            ContentSafetyStage.ToolResult,
                            ContentSafetyDetectionTypes.Unavailable,
                            category: null,
                            severity: null,
                            threshold: null),
                        Subject: $"Tool result from '{toolName}'"), cancellationToken).ConfigureAwait(false);

                    if (cfg.OnUnavailable == ContentSafetyFailPolicy.FailClosed)
                    {
                        string substitute = BuildBlockedSubstitute(
                            toolName,
                            ContentSafetyDetectionTypes.Unavailable,
                            category: null,
                            severity: null);
                        return ContentSafetyToolResultOutcome.Blocked(substitute, ContentSafetyDetectionTypes.Unavailable);
                    }
                    return ContentSafetyToolResultOutcome.PassThrough(toolResultJson);
                }
            case ContentSafetyDecision.Flagged:
                {
                    (string? category, int? severity) = GuardrailAuditFields.PickCategoryAndSeverity(evaluation);
                    string detectionType = ContentSafetyDetectionTypes.ForResultWithShield(
                        evaluation,
                        preferIndirect: true);
                    int? threshold = GuardrailAuditFields.ThresholdFor(cfg, category);
                    await _log.LogAsync(new SuspiciousRequest(
                        Guid.NewGuid().ToString("N"),
                        DateTime.UtcNow,
                        $"Tool result from '{toolName}' flagged by Content Safety",
                        detectionType,
                        userId,
                        ContentSafetyActions.Flagged,
                        Category: category,
                        Severity: severity,
                        Decision: evaluation.Decision.ToString(),
                        Stage: ContentSafetyStage.ToolResult.ToString(),
                        Threshold: threshold,
                        Reason: GuardrailAuditFields.BuildReason(
                            evaluation,
                            ContentSafetyStage.ToolResult,
                            detectionType,
                            category,
                            severity,
                            threshold),
                        Subject: $"Tool result from '{toolName}'"), cancellationToken).ConfigureAwait(false);
                    return ContentSafetyToolResultOutcome.PassThrough(toolResultJson);
                }

            case ContentSafetyDecision.Passed:
            default:
                return ContentSafetyToolResultOutcome.PassThrough(toolResultJson);
        }
    }

    private static string BuildBlockedSubstitute(
        string toolName,
        string detectionType,
        string? category,
        int? severity)
    {
        string reason = BuildBlockedReason(category, severity, detectionType);
        var envelope = new JsonObject
        {
            ["_content_safety"] = new JsonObject
            {
                ["blocked"] = true,
                ["tool"] = toolName,
                ["detection_type"] = detectionType,
                ["category"] = category,
                ["severity"] = severity,
                ["reason"] = reason,
                ["message_for_user"] = $"The result from {toolName} was withheld because {reason}.",
                ["note"] = "The tool result was blocked by the Content Safety layer. Do not re-issue the "
                    + "same call. Tell the user that the requested data was withheld and why.",
            }
        };
        return envelope.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static string BuildBlockedReason(string? category, int? severity, string detectionType)
    {
        if (string.Equals(detectionType, ContentSafetyDetectionTypes.Unavailable, StringComparison.Ordinal))
        {
            return "the Content Safety service was unavailable and this deployment is configured to fail closed";
        }

        string categoryText = string.IsNullOrWhiteSpace(category)
            ? "a configured Content Safety category"
            : $"{category} content";

        string severityText = severity.HasValue
            ? $" at {DescribeSeverity(severity.Value)} severity"
            : string.Empty;

        return $"Content Safety classified it as {categoryText}{severityText}";
    }

    private static string DescribeSeverity(int severity) => severity switch
    {
        <= 0 => "low",
        <= 2 => "medium",
        <= 4 => "high",
        _ => "severe",
    };

}

/// <summary>Outcome returned by <see cref="ContentSafetyToolResultInspector.InspectAsync"/>.</summary>
public readonly record struct ContentSafetyToolResultOutcome(
    string Payload,
    bool WasBlocked,
    string? DetectionType)
{
    public static ContentSafetyToolResultOutcome PassThrough(string payload) =>
        new(payload, WasBlocked: false, DetectionType: null);

    public static ContentSafetyToolResultOutcome Blocked(string payload, string detectionType) =>
        new(payload, WasBlocked: true, DetectionType: detectionType);
}

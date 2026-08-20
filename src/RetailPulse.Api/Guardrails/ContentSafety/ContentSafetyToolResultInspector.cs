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

        var ctx = new ContentSafetyEvaluationContext(
            UserId: userId,
            SourceId: toolName,
            CheckPromptShield: false);

        ContentSafetyResult evaluation = await _evaluator.EvaluateAsync(
            toolResultJson,
            ContentSafetyStage.ToolResult,
            ctx,
            cancellationToken).ConfigureAwait(false);

        switch (evaluation.Decision)
        {
            case ContentSafetyDecision.Blocked:
                {
                    // Tool-result stage does not run Prompt Shields, so a
                    // block without a category is never a prompt-shield hit.
                    string detectionType = ContentSafetyDetectionTypes.ForResultWithoutShield(evaluation);
                    (string? category, int? severity) = PickCategoryAndSeverity(evaluation);

                    await _log.LogAsync(new SuspiciousRequest(
                        Guid.NewGuid().ToString("N"),
                        DateTime.UtcNow,
                        $"Tool result from '{toolName}' blocked by Content Safety",
                        detectionType,
                        userId,
                        ContentSafetyActions.Blocked,
                        Category: category,
                        Severity: severity,
                        Decision: evaluation.Decision.ToString()), cancellationToken).ConfigureAwait(false);

                    _logger.LogWarning(
                        "Content Safety blocked tool result from '{Tool}' (decision={Decision}, categories={CategoryCount})",
                        toolName, evaluation.Decision, evaluation.Categories.Count);

                    string substitute = BuildBlockedSubstitute(toolName, detectionType);
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
                        Decision: evaluation.Decision.ToString()), cancellationToken).ConfigureAwait(false);

                    if (cfg.OnUnavailable == ContentSafetyFailPolicy.FailClosed)
                    {
                        string substitute = BuildBlockedSubstitute(toolName, ContentSafetyDetectionTypes.Unavailable);
                        return ContentSafetyToolResultOutcome.Blocked(substitute, ContentSafetyDetectionTypes.Unavailable);
                    }
                    return ContentSafetyToolResultOutcome.PassThrough(toolResultJson);
                }
            case ContentSafetyDecision.Flagged:
                {
                    (string? category, int? severity) = PickCategoryAndSeverity(evaluation);
                    await _log.LogAsync(new SuspiciousRequest(
                        Guid.NewGuid().ToString("N"),
                        DateTime.UtcNow,
                        $"Tool result from '{toolName}' flagged by Content Safety",
                        ContentSafetyDetectionTypes.ForResultWithoutShield(evaluation),
                        userId,
                        ContentSafetyActions.Flagged,
                        Category: category,
                        Severity: severity,
                        Decision: evaluation.Decision.ToString()), cancellationToken).ConfigureAwait(false);
                    return ContentSafetyToolResultOutcome.PassThrough(toolResultJson);
                }

            case ContentSafetyDecision.Passed:
            default:
                return ContentSafetyToolResultOutcome.PassThrough(toolResultJson);
        }
    }

    private static string BuildBlockedSubstitute(string toolName, string detectionType)
    {
        var envelope = new JsonObject
        {
            ["_content_safety"] = new JsonObject
            {
                ["blocked"] = true,
                ["tool"] = toolName,
                ["detection_type"] = detectionType,
                ["note"] = "The tool result was blocked by the Content Safety layer. Do not re-issue the "
                    + "same call. Explain to the user that the requested data cannot be included.",
            }
        };
        return envelope.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static (string? Category, int? Severity) PickCategoryAndSeverity(ContentSafetyResult evaluation)
    {
        if (evaluation.Categories.Count == 0) return (null, null);
        ContentSafetyCategoryHit top = evaluation.Categories[0];
        for (int i = 1; i < evaluation.Categories.Count; i++)
        {
            if (evaluation.Categories[i].Severity > top.Severity)
                top = evaluation.Categories[i];
        }
        return (top.Category, top.Severity);
    }
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

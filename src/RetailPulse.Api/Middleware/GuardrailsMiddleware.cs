using System.Diagnostics;
using System.Text.RegularExpressions;
using RetailPulse.Api.Guardrails;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Api.Middleware;

/// <summary>
/// Guardrails middleware that filters input (jailbreak, injection, access control)
/// and output (PII redaction). Transparent to agents — operates on ChatRequest/ChatResponse.
/// Uses <see cref="GuardrailPatterns"/> for compiled regex matching and
/// <see cref="GuardrailsConfig"/> (from Contracts) for runtime toggles.
/// </summary>
public class GuardrailsMiddleware
{
    private readonly GuardrailsConfig _config;
    private readonly ISuspiciousRequestLog _suspiciousLog;
    private readonly ITenantProvider _tenantProvider;
    private readonly ILogger<GuardrailsMiddleware> _logger;
    private readonly IContentSafetyEvaluator _contentSafety;

    /// <summary>
    /// SQL injection patterns — case-insensitive substring matching.
    /// </summary>
    private static readonly string[] _injectionPatterns =
    [
        "'; drop table", "'; delete from", "' or '1'='1", "' or 1=1--",
        "union select", "<script>", "</script>", "javascript:",
        "onerror=", "onload=", "<iframe", "<img src=x onerror"
    ];

    /// <summary>
    /// Friendly refusal template. Supports {type} placeholder.
    /// </summary>
    private const string _defaultRefusal =
        "I can't help with that request. My guardrails detected potentially harmful content ({type}). " +
        "Please rephrase your question about retail operations and I'll be happy to assist.";

    private const string _unavailableRefusal =
        "I can't process that request right now. The content safety layer is temporarily unavailable and " +
        "this deployment is configured to fail closed. Please retry shortly.";

    public GuardrailsMiddleware(
        GuardrailsConfig config,
        ISuspiciousRequestLog suspiciousLog,
        ITenantProvider tenantProvider,
        ILogger<GuardrailsMiddleware> logger,
        IContentSafetyEvaluator? contentSafety = null)
    {
        _config = config;
        _suspiciousLog = suspiciousLog;
        _tenantProvider = tenantProvider;
        _logger = logger;
        _contentSafety = contentSafety ?? NoOpContentSafetyEvaluator.Instance;
    }

    /// <summary>
    /// Checks input for jailbreak, injection, and access control violations.
    /// Returns <see cref="GuardrailResult.Passed"/> if clean.
    /// </summary>
    public async Task<GuardrailResult> CheckInputAsync(ChatRequest request, CancellationToken ct = default)
    {
        using Activity? activity = AgentTelemetry.Source.StartActivity("guardrails.input_check", ActivityKind.Internal);
        string message = request.Message;
        string userId = request.User?.ObjectId ?? "anonymous";

        // ── Input length gate ────────────────────────────────────────────
        if (message.Length > _config.MaxInputLength)
        {
            activity?.SetTag("guardrails.blocked", true);
            activity?.SetTag("guardrails.type", "input_too_long");
            return GuardrailResult.Blocked(
                $"Input exceeds the maximum allowed length of {_config.MaxInputLength} characters.");
        }

        // ── Jailbreak detection (compiled regex patterns) ────────────────
        if (_config.JailbreakDetectionEnabled)
        {
            IReadOnlyList<string> jailbreakHits = GuardrailPatterns.DetectJailbreak(message);
            if (jailbreakHits.Count > 0)
            {
                activity?.SetTag("guardrails.blocked", true);
                activity?.SetTag("guardrails.type", "jailbreak");
                activity?.SetTag("guardrails.patterns", string.Join(",", jailbreakHits));

                await _suspiciousLog.LogAsync(new SuspiciousRequest(
                    Guid.NewGuid().ToString("N"),
                    DateTime.UtcNow,
                    Truncate(message, 200),
                    "jailbreak",
                    userId,
                    "blocked"), ct);

                _logger.LogWarning("Jailbreak attempt blocked from user {UserId}: patterns [{Patterns}]",
                    userId, string.Join(", ", jailbreakHits));

                return GuardrailResult.Blocked(_defaultRefusal.Replace("{type}", "jailbreak attempt"));
            }
        }

        // ── Injection detection (substring matching) ─────────────────────
        if (_config.JailbreakDetectionEnabled) // injection piggybacks on the jailbreak toggle
        {
            string lower = message.ToLowerInvariant();
            string? injectionMatch = _injectionPatterns.FirstOrDefault(p => lower.Contains(p));
            if (injectionMatch is not null)
            {
                activity?.SetTag("guardrails.blocked", true);
                activity?.SetTag("guardrails.type", "injection");

                await _suspiciousLog.LogAsync(new SuspiciousRequest(
                    Guid.NewGuid().ToString("N"),
                    DateTime.UtcNow,
                    Truncate(message, 200),
                    "injection",
                    userId,
                    "blocked"), ct);

                _logger.LogWarning("Injection attempt blocked from user {UserId}: matched '{Pattern}'",
                    userId, injectionMatch);

                return GuardrailResult.Blocked(_defaultRefusal.Replace("{type}", "potential injection"));
            }
        }

        // ── PII in input — log but don't block (redact output instead) ───
        if (_config.PiiDetectionEnabled)
        {
            IReadOnlyList<string> piiHits = GuardrailPatterns.DetectPii(message);
            if (piiHits.Count > 0)
            {
                activity?.SetTag("guardrails.pii_in_input", string.Join(",", piiHits));
                _logger.LogInformation("PII detected in input from user {UserId}: [{Types}]",
                    userId, string.Join(", ", piiHits));
            }
        }

        // ── Content Safety (remote, optional) — runs only after the pattern
        //     layer has passed. On disabled path the evaluator is a no-op.
        ContentSafetyConfig cs = _config.ContentSafety;
        if (cs.Enabled && cs.CheckInput)
        {
            ContentSafetyResult evaluation = await _contentSafety.EvaluateAsync(
                message,
                ContentSafetyStage.Input,
                new ContentSafetyEvaluationContext(
                    UserId: userId,
                    CheckPromptShield: cs.PromptShieldsEnabled),
                ct).ConfigureAwait(false);

            GuardrailResult? contentSafetyOutcome = await HandleContentSafetyDecisionAsync(
                evaluation,
                message,
                userId,
                activity,
                ct).ConfigureAwait(false);
            if (contentSafetyOutcome is not null)
            {
                return contentSafetyOutcome;
            }
        }

        activity?.SetTag("guardrails.blocked", false);
        return GuardrailResult.Passed();
    }

    /// <summary>
    /// Filters output for PII, replacing sensitive data with [REDACTED:{type}] markers.
    /// </summary>
    public async Task<string> FilterOutputAsync(string response, string userId, CancellationToken ct = default)
    {
        ContentSafetyConfig cs = _config.ContentSafety;
        bool piiEnabled = _config.PiiDetectionEnabled && _config.AutoRedactPii;
        bool contentSafetyEnabled = cs.Enabled && cs.CheckOutput;
        if (!piiEnabled && !contentSafetyEnabled)
            return response;

        using Activity? activity = AgentTelemetry.Source.StartActivity("guardrails.output_filter", ActivityKind.Internal);
        string redacted = response;
        int redactionCount = 0;

        if (piiEnabled)
        {
            foreach ((string? name, Regex? pattern) in GuardrailPatterns.PiiPatterns)
            {
                MatchCollection matches = pattern.Matches(redacted);
                if (matches.Count > 0)
                {
                    redactionCount += matches.Count;
                    redacted = pattern.Replace(redacted, $"[REDACTED:{name.ToUpperInvariant()}]");
                }
            }
        }

        activity?.SetTag("guardrails.pii_redactions", redactionCount);

        if (redactionCount > 0)
        {
            await _suspiciousLog.LogAsync(new SuspiciousRequest(
                Guid.NewGuid().ToString("N"),
                DateTime.UtcNow,
                $"PII redacted from output ({redactionCount} items)",
                "pii",
                userId,
                "redacted"), ct);

            _logger.LogInformation("Redacted {Count} PII items from response for user {UserId}",
                redactionCount, userId);
        }

        if (contentSafetyEnabled)
        {
            ContentSafetyResult evaluation = await _contentSafety.EvaluateAsync(
                redacted,
                ContentSafetyStage.Output,
                new ContentSafetyEvaluationContext(UserId: userId, CheckPromptShield: false),
                ct).ConfigureAwait(false);

            string? substitute = await HandleContentSafetyOutputAsync(
                evaluation, redacted, userId, activity, ct).ConfigureAwait(false);
            if (substitute is not null)
            {
                return substitute;
            }
        }

        return redacted;
    }

    private async Task<GuardrailResult?> HandleContentSafetyDecisionAsync(
        ContentSafetyResult evaluation,
        string message,
        string userId,
        Activity? activity,
        CancellationToken ct)
    {
        ContentSafetyConfig cs = _config.ContentSafety;
        switch (evaluation.Decision)
        {
            case ContentSafetyDecision.Blocked:
                {
                    string detectionType =
                        ContentSafetyDetectionTypes.ForResultWithShield(evaluation);
                    (string? category, int? severity) = PickCategoryAndSeverity(evaluation);

                    activity?.SetTag("guardrails.blocked", true);
                    activity?.SetTag("guardrails.type", detectionType);

                    await _suspiciousLog.LogAsync(new SuspiciousRequest(
                        Guid.NewGuid().ToString("N"),
                        DateTime.UtcNow,
                        Truncate(message, 200),
                        detectionType,
                        userId,
                        ContentSafetyActions.Blocked,
                        Category: category,
                        Severity: severity,
                        Decision: evaluation.Decision.ToString()), ct).ConfigureAwait(false);

                    _logger.LogWarning(
                        "Content Safety blocked input from user {UserId}: type={DetectionType}, categories={CategoryCount}",
                        userId, detectionType, evaluation.Categories.Count);

                    return GuardrailResult.Blocked(_defaultRefusal.Replace("{type}", "content safety"));
                }
            case ContentSafetyDecision.ServiceUnavailable:
                {
                    string action = cs.OnUnavailable == ContentSafetyFailPolicy.FailClosed
                        ? ContentSafetyActions.FailClosedBlocked
                        : ContentSafetyActions.FailOpenPassed;

                    await _suspiciousLog.LogAsync(new SuspiciousRequest(
                        Guid.NewGuid().ToString("N"),
                        DateTime.UtcNow,
                        Truncate(message, 200),
                        ContentSafetyDetectionTypes.Unavailable,
                        userId,
                        action,
                        Category: null,
                        Severity: null,
                        Decision: evaluation.Decision.ToString()), ct).ConfigureAwait(false);

                    if (cs.OnUnavailable == ContentSafetyFailPolicy.FailClosed)
                    {
                        activity?.SetTag("guardrails.blocked", true);
                        activity?.SetTag("guardrails.type", ContentSafetyDetectionTypes.Unavailable);
                        _logger.LogWarning(
                            "Content Safety unavailable for user {UserId}; fail-closed policy blocking request.",
                            userId);
                        return GuardrailResult.Blocked(_unavailableRefusal);
                    }

                    _logger.LogInformation(
                        "Content Safety unavailable for user {UserId}; fail-open policy allowing request.",
                        userId);
                    return null;
                }
            case ContentSafetyDecision.Flagged:
                {
                    (string? category, int? severity) = PickCategoryAndSeverity(evaluation);
                    await _suspiciousLog.LogAsync(new SuspiciousRequest(
                        Guid.NewGuid().ToString("N"),
                        DateTime.UtcNow,
                        Truncate(message, 200),
                        ContentSafetyDetectionTypes.ForResultWithShield(evaluation),
                        userId,
                        ContentSafetyActions.Flagged,
                        Category: category,
                        Severity: severity,
                        Decision: evaluation.Decision.ToString()), ct).ConfigureAwait(false);
                    return null;
                }

            case ContentSafetyDecision.Passed:
            default:
                return null;
        }
    }

    private async Task<string?> HandleContentSafetyOutputAsync(
        ContentSafetyResult evaluation,
        string response,
        string userId,
        Activity? activity,
        CancellationToken ct)
    {
        ContentSafetyConfig cs = _config.ContentSafety;
        switch (evaluation.Decision)
        {
            case ContentSafetyDecision.Blocked:
                {
                    // Output stage never runs Prompt Shields, so an output-only
                    // block without a category must NEVER be labeled prompt-shield.
                    string detectionType = ContentSafetyDetectionTypes.ForResultWithoutShield(evaluation);
                    (string? category, int? severity) = PickCategoryAndSeverity(evaluation);
                    activity?.SetTag("guardrails.output_blocked", true);
                    activity?.SetTag("guardrails.type", detectionType);

                    await _suspiciousLog.LogAsync(new SuspiciousRequest(
                        Guid.NewGuid().ToString("N"),
                        DateTime.UtcNow,
                        Truncate(response, 200),
                        detectionType,
                        userId,
                        ContentSafetyActions.Blocked,
                        Category: category,
                        Severity: severity,
                        Decision: evaluation.Decision.ToString()), ct).ConfigureAwait(false);

                    _logger.LogWarning(
                        "Content Safety blocked model output for user {UserId}: type={DetectionType}",
                        userId, detectionType);

                    return _defaultRefusal.Replace("{type}", "content safety");
                }
            case ContentSafetyDecision.ServiceUnavailable:
                {
                    string action = cs.OnUnavailable == ContentSafetyFailPolicy.FailClosed
                        ? ContentSafetyActions.FailClosedBlocked
                        : ContentSafetyActions.FailOpenPassed;

                    await _suspiciousLog.LogAsync(new SuspiciousRequest(
                        Guid.NewGuid().ToString("N"),
                        DateTime.UtcNow,
                        Truncate(response, 200),
                        ContentSafetyDetectionTypes.Unavailable,
                        userId,
                        action,
                        Category: null,
                        Severity: null,
                        Decision: evaluation.Decision.ToString()), ct).ConfigureAwait(false);

                    return cs.OnUnavailable == ContentSafetyFailPolicy.FailClosed
                        ? _unavailableRefusal
                        : null;
                }
            case ContentSafetyDecision.Flagged:
                {
                    (string? category, int? severity) = PickCategoryAndSeverity(evaluation);
                    await _suspiciousLog.LogAsync(new SuspiciousRequest(
                        Guid.NewGuid().ToString("N"),
                        DateTime.UtcNow,
                        Truncate(response, 200),
                        ContentSafetyDetectionTypes.ForResultWithoutShield(evaluation),
                        userId,
                        ContentSafetyActions.Flagged,
                        Category: category,
                        Severity: severity,
                        Decision: evaluation.Decision.ToString()), ct).ConfigureAwait(false);
                    return null;
                }

            case ContentSafetyDecision.Passed:
            default:
                return null;
        }
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    private static (string? Category, int? Severity) PickCategoryAndSeverity(ContentSafetyResult evaluation)
    {
        if (evaluation.Categories.Count == 0) return (null, null);
        // Highest-severity category wins for audit — matches the "most
        // severe hit" convention operators expect on the dashboard.
        ContentSafetyCategoryHit top = evaluation.Categories[0];
        for (int i = 1; i < evaluation.Categories.Count; i++)
        {
            if (evaluation.Categories[i].Severity > top.Severity)
                top = evaluation.Categories[i];
        }
        return (top.Category, top.Severity);
    }
}

/// <summary>
/// Result of a guardrails input check.
/// </summary>
public class GuardrailResult
{
    public bool IsBlocked { get; init; }
    public string? RefusalMessage { get; init; }

    public static GuardrailResult Passed() => new() { IsBlocked = false };
    public static GuardrailResult Blocked(string message) => new() { IsBlocked = true, RefusalMessage = message };
}

using System.Diagnostics;
using RetailPulse.Api.Guardrails;
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

    /// <summary>
    /// SQL injection patterns — case-insensitive substring matching.
    /// </summary>
    private static readonly string[] InjectionPatterns =
    [
        "'; drop table", "'; delete from", "' or '1'='1", "' or 1=1--",
        "union select", "<script>", "</script>", "javascript:",
        "onerror=", "onload=", "<iframe", "<img src=x onerror"
    ];

    /// <summary>
    /// Friendly refusal template. Supports {type} placeholder.
    /// </summary>
    private const string DefaultRefusal =
        "I can't help with that request. My guardrails detected potentially harmful content ({type}). " +
        "Please rephrase your question about retail operations and I'll be happy to assist.";

    public GuardrailsMiddleware(
        GuardrailsConfig config,
        ISuspiciousRequestLog suspiciousLog,
        ITenantProvider tenantProvider,
        ILogger<GuardrailsMiddleware> logger)
    {
        _config = config;
        _suspiciousLog = suspiciousLog;
        _tenantProvider = tenantProvider;
        _logger = logger;
    }

    /// <summary>
    /// Checks input for jailbreak, injection, and access control violations.
    /// Returns <see cref="GuardrailResult.Passed"/> if clean.
    /// </summary>
    public async Task<GuardrailResult> CheckInputAsync(ChatRequest request, CancellationToken ct = default)
    {
        using var activity = AgentTelemetry.Source.StartActivity("guardrails.input_check", ActivityKind.Internal);
        var message = request.Message;
        var userId = request.User?.ObjectId ?? "anonymous";

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
            var jailbreakHits = GuardrailPatterns.DetectJailbreak(message);
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

                return GuardrailResult.Blocked(DefaultRefusal.Replace("{type}", "jailbreak attempt"));
            }
        }

        // ── Injection detection (substring matching) ─────────────────────
        if (_config.JailbreakDetectionEnabled) // injection piggybacks on the jailbreak toggle
        {
            var lower = message.ToLowerInvariant();
            var injectionMatch = InjectionPatterns.FirstOrDefault(p => lower.Contains(p));
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

                return GuardrailResult.Blocked(DefaultRefusal.Replace("{type}", "potential injection"));
            }
        }

        // ── PII in input — log but don't block (redact output instead) ───
        if (_config.PiiDetectionEnabled)
        {
            var piiHits = GuardrailPatterns.DetectPii(message);
            if (piiHits.Count > 0)
            {
                activity?.SetTag("guardrails.pii_in_input", string.Join(",", piiHits));
                _logger.LogInformation("PII detected in input from user {UserId}: [{Types}]",
                    userId, string.Join(", ", piiHits));
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
        if (!_config.PiiDetectionEnabled || !_config.AutoRedactPii)
            return response;

        using var activity = AgentTelemetry.Source.StartActivity("guardrails.output_filter", ActivityKind.Internal);
        var redacted = response;
        var redactionCount = 0;

        foreach (var (name, pattern) in GuardrailPatterns.PiiPatterns)
        {
            var matches = pattern.Matches(redacted);
            if (matches.Count > 0)
            {
                redactionCount += matches.Count;
                redacted = pattern.Replace(redacted, $"[REDACTED:{name.ToUpperInvariant()}]");
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

        return redacted;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";
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

using Microsoft.Extensions.Logging;
using RetailPulse.Api.Agents.Tools;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Api.Models;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Api.Guardrails.AgentDefinition;

/// <summary>
/// Load-time safety and policy validator for the fully hydrated
/// <see cref="PromptConfiguration"/>. Runs once during <c>Program.cs</c>
/// startup — after tenant-placeholder hydration, before any agent is
/// constructed and before <c>AddAgentRouting</c> composes the pipeline — so
/// hostile, mis-typed, or policy-violating definitions never reach a running
/// deployment.
/// </summary>
/// <remarks>
/// The validator enforces three stacked layers described in issue #99:
/// <list type="number">
///   <item>Structural — required fields, numeric bounds, model allow-list, role enum.</item>
///   <item>Policy — tool allow-list membership and explicit privileged-tool grants.</item>
///   <item>Safety — pattern-layer jailbreak scan first (cheap, deterministic), then
///     the optional Content Safety second pass with Prompt Shields for
///     instruction-override / role-reversal / exfiltration coverage.</item>
/// </list>
/// Every rejection or quarantine is audited via
/// <see cref="ISuspiciousRequestLog"/> with an operator-actionable message —
/// raw prompt text is never included in the audit row.
/// </remarks>
public sealed class AgentDefinitionValidator
{
    private static readonly HashSet<string> _validRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "specialist",
        "orchestration",
        "router",
        "bespoke",
    };

    private readonly GuardrailsConfig _guardrails;
    private readonly JailbreakDetector _jailbreak;
    private readonly IContentSafetyEvaluator _contentSafety;
    private readonly ISuspiciousRequestLog _audit;
    private readonly AgentToolRegistry _tools;
    private readonly ILogger<AgentDefinitionValidator> _logger;

    public AgentDefinitionValidator(
        GuardrailsConfig guardrails,
        JailbreakDetector jailbreak,
        IContentSafetyEvaluator contentSafety,
        ISuspiciousRequestLog audit,
        AgentToolRegistry tools,
        ILogger<AgentDefinitionValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(guardrails);
        ArgumentNullException.ThrowIfNull(jailbreak);
        ArgumentNullException.ThrowIfNull(contentSafety);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(logger);

        _guardrails = guardrails;
        _jailbreak = jailbreak;
        _contentSafety = contentSafety;
        _audit = audit;
        _tools = tools;
        _logger = logger;
    }

    /// <summary>
    /// Validate every definition in <paramref name="config"/> and apply the
    /// configured failure policy. Under
    /// <see cref="AgentDefinitionFailurePolicy.QuarantineOffender"/> offending
    /// definitions are removed from <see cref="PromptConfiguration.Agents"/>
    /// so downstream composition never sees them.
    /// </summary>
    public async Task<AgentDefinitionValidationReport> ValidateAsync(
        PromptConfiguration config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        AgentDefinitionPolicy policy = _guardrails.AgentDefinition;
        bool safetyChecks = policy.SafetyChecksEnabled && _guardrails.ContentSafety.Enabled;

        var allViolations = new List<AgentDefinitionViolation>();
        var offenderKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int contentSafetyCalls = 0;

        // Duplicate-name detection runs across the whole roster.
        AddDuplicateNameViolations(config, allViolations, offenderKeys);

        foreach ((string sectionKey, Models.AgentDefinition def) in config.Agents.ToArray())
        {
            string agentKey = string.IsNullOrWhiteSpace(def.Key) ? sectionKey : def.Key;
            var perAgent = new List<AgentDefinitionViolation>();

            ValidateStructural(agentKey, def, policy, perAgent);
            ValidatePolicy(agentKey, def, policy, perAgent);

            bool patternBlocked = ValidatePatterns(agentKey, def, perAgent);

            if (!patternBlocked && safetyChecks)
            {
                int calls = await ValidateContentSafetyAsync(agentKey, def, perAgent, cancellationToken)
                    .ConfigureAwait(false);
                contentSafetyCalls += calls;
            }

            if (perAgent.Count > 0)
            {
                offenderKeys.Add(sectionKey);
                allViolations.AddRange(perAgent);
            }
        }

        AgentDefinitionFailurePolicy failurePolicy = policy.OnValidationFailure;
        string auditAction = failurePolicy == AgentDefinitionFailurePolicy.QuarantineOffender
            ? AgentDefinitionDetectionTypes.ActionQuarantined
            : AgentDefinitionDetectionTypes.ActionBlocked;

        foreach (AgentDefinitionViolation violation in allViolations)
        {
            await WriteAuditAsync(violation, auditAction, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "AgentDefinitionValidator scanned {AgentCount} definition(s) with {ViolationCount} violation(s) " +
            "and {ContentSafetyCalls} content-safety call(s). Policy: {FailurePolicy}.",
            config.Agents.Count, allViolations.Count, contentSafetyCalls, failurePolicy);

        if (offenderKeys.Count == 0)
        {
            return new AgentDefinitionValidationReport(allViolations, [], failurePolicy);
        }

        if (failurePolicy == AgentDefinitionFailurePolicy.QuarantineOffender)
        {
            var quarantined = new List<string>();
            foreach (string offenderKey in offenderKeys)
            {
                if (config.Agents.Remove(offenderKey))
                {
                    quarantined.Add(offenderKey);
                    string summary = SummarizeReasons(allViolations, offenderKey);
                    _logger.LogWarning(
                        "Quarantined agent definition {AgentKey}: {ReasonSummary}",
                        offenderKey, summary);
                }
            }

            return new AgentDefinitionValidationReport(allViolations, quarantined, failurePolicy);
        }

        throw new AgentDefinitionValidationException(allViolations);
    }

    private static void ValidateStructural(
        string agentKey,
        Models.AgentDefinition def,
        AgentDefinitionPolicy policy,
        List<AgentDefinitionViolation> violations)
    {
        if (string.IsNullOrWhiteSpace(def.Name))
        {
            violations.Add(new AgentDefinitionViolation(
                agentKey, "Name", "structural.required",
                "Name is required and cannot be blank.",
                AgentDefinitionDetectionTypes.Structural));
        }

        if (string.IsNullOrWhiteSpace(def.Model))
        {
            violations.Add(new AgentDefinitionViolation(
                agentKey, "Model", "structural.required",
                "Model is required and cannot be blank.",
                AgentDefinitionDetectionTypes.Structural));
        }
        else if (policy.AllowedModels.Count > 0
                 && !policy.AllowedModels.Any(m => string.Equals(m, def.Model, StringComparison.OrdinalIgnoreCase)))
        {
            violations.Add(new AgentDefinitionViolation(
                agentKey, "Model", "structural.model-not-allowed",
                $"Model '{def.Model}' is not in the deployment allow-list ({string.Join(", ", policy.AllowedModels)}).",
                AgentDefinitionDetectionTypes.Structural));
        }

        double min = policy.TemperatureBounds.Min;
        double max = policy.TemperatureBounds.Max;
        if (def.Temperature < min || def.Temperature > max)
        {
            violations.Add(new AgentDefinitionViolation(
                agentKey, "Temperature", "structural.temperature-out-of-bounds",
                $"Temperature {def.Temperature:0.###} is outside the permitted range [{min:0.###}, {max:0.###}].",
                AgentDefinitionDetectionTypes.Structural));
        }

        if (def.SystemPrompt.Length > policy.MaxSystemPromptLength)
        {
            violations.Add(new AgentDefinitionViolation(
                agentKey, "SystemPrompt", "structural.system-prompt-too-long",
                $"SystemPrompt length {def.SystemPrompt.Length} exceeds MaxSystemPromptLength {policy.MaxSystemPromptLength}.",
                AgentDefinitionDetectionTypes.Structural));
        }

        for (int i = 0; i < def.KeywordFastPaths.Count; i++)
        {
            string phrase = def.KeywordFastPaths[i] ?? string.Empty;
            if (phrase.Length > policy.MaxKeywordFastPathLength)
            {
                violations.Add(new AgentDefinitionViolation(
                    agentKey, $"KeywordFastPaths[{i}]", "structural.keyword-too-long",
                    $"KeywordFastPaths[{i}] length {phrase.Length} exceeds MaxKeywordFastPathLength {policy.MaxKeywordFastPathLength}.",
                    AgentDefinitionDetectionTypes.Structural));
            }
        }

        if (!string.IsNullOrWhiteSpace(def.Role) && !_validRoles.Contains(def.Role))
        {
            violations.Add(new AgentDefinitionViolation(
                agentKey, "Role", "structural.role-not-recognized",
                $"Role '{def.Role}' is not one of specialist|orchestration|router|bespoke.",
                AgentDefinitionDetectionTypes.Structural));
        }

        if (def.ScorecardWeight is < 0.0 or > 1.0)
        {
            violations.Add(new AgentDefinitionViolation(
                agentKey, "ScorecardWeight", "structural.scorecard-weight-out-of-bounds",
                $"ScorecardWeight {def.ScorecardWeight:0.###} is outside [0, 1].",
                AgentDefinitionDetectionTypes.Structural));
        }
    }

    private void ValidatePolicy(
        string agentKey,
        Models.AgentDefinition def,
        AgentDefinitionPolicy policy,
        List<AgentDefinitionViolation> violations)
    {
        bool useAllowedTools = policy.AllowedTools.Count > 0;
        var privilegedGrantByTool = new Dictionary<string, PrivilegedToolGrant>(StringComparer.OrdinalIgnoreCase);
        foreach (PrivilegedToolGrant grant in policy.PrivilegedTools)
        {
            if (!string.IsNullOrWhiteSpace(grant.Tool))
            {
                privilegedGrantByTool[grant.Tool] = grant;
            }
        }

        for (int i = 0; i < def.Tools.Count; i++)
        {
            string raw = def.Tools[i] ?? string.Empty;
            string toolName = raw.Trim();
            if (toolName.Length == 0)
            {
                continue;
            }

            bool inAllowList = useAllowedTools
                ? policy.AllowedTools.Any(t => string.Equals(t, toolName, StringComparison.OrdinalIgnoreCase))
                : _tools.Contains(toolName);

            if (!inAllowList)
            {
                string source = useAllowedTools ? "deployment AllowedTools list" : "AgentToolRegistry";
                violations.Add(new AgentDefinitionViolation(
                    agentKey, $"Tools[{i}]", "policy.tool-not-allowed",
                    $"Tool '{toolName}' is not permitted — not present in the {source}.",
                    AgentDefinitionDetectionTypes.Policy));
            }

            if (privilegedGrantByTool.TryGetValue(toolName, out PrivilegedToolGrant? grant))
            {
                bool granted = grant.GrantedTo.Any(g => string.Equals(g, agentKey, StringComparison.OrdinalIgnoreCase));
                if (!granted)
                {
                    violations.Add(new AgentDefinitionViolation(
                        agentKey, $"Tools[{i}]", "policy.tool-not-granted",
                        $"Privileged tool '{toolName}' cannot be self-asserted by agent '{agentKey}'. " +
                        "Add the agent key to the PrivilegedTools grant in deployment configuration.",
                        AgentDefinitionDetectionTypes.PrivilegedGrant));
                }
            }
        }
    }

    private bool ValidatePatterns(
        string agentKey,
        Models.AgentDefinition def,
        List<AgentDefinitionViolation> violations)
    {
        bool blocked = false;

        blocked |= AddJailbreakViolation(agentKey, "SystemPrompt", def.SystemPrompt, violations);
        blocked |= AddJailbreakViolation(agentKey, "DisplayName", def.DisplayName, violations);
        blocked |= AddJailbreakViolation(agentKey, "FallbackReply", def.FallbackReply, violations);

        for (int i = 0; i < def.KeywordFastPaths.Count; i++)
        {
            string phrase = def.KeywordFastPaths[i] ?? string.Empty;
            blocked |= AddJailbreakViolation(agentKey, $"KeywordFastPaths[{i}]", phrase, violations);
        }

        return blocked;
    }

    private bool AddJailbreakViolation(
        string agentKey,
        string field,
        string text,
        List<AgentDefinitionViolation> violations)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        IReadOnlyList<string> patternHits = GuardrailPatterns.DetectJailbreak(text);
        string? substringHit = _jailbreak.GetMatchedPattern(text);

        if (patternHits.Count == 0 && substringHit is null)
        {
            return false;
        }

        var names = new List<string>(patternHits);
        if (substringHit is { Length: > 0 } && !names.Any(n =>
                string.Equals(n, substringHit, StringComparison.OrdinalIgnoreCase)))
        {
            names.Add(substringHit);
        }

        string reason = string.Join(", ", names);
        violations.Add(new AgentDefinitionViolation(
            agentKey, field, "safety.pattern-jailbreak",
            $"Pattern-layer jailbreak indicator(s) in {field}: [{reason}].",
            AgentDefinitionDetectionTypes.Jailbreak));
        return true;
    }

    private async Task<int> ValidateContentSafetyAsync(
        string agentKey,
        Models.AgentDefinition def,
        List<AgentDefinitionViolation> violations,
        CancellationToken cancellationToken)
    {
        int calls = 0;
        calls += await EvaluateFieldAsync(agentKey, "SystemPrompt", def.SystemPrompt, violations, cancellationToken)
            .ConfigureAwait(false);
        calls += await EvaluateFieldAsync(agentKey, "DisplayName", def.DisplayName, violations, cancellationToken)
            .ConfigureAwait(false);
        calls += await EvaluateFieldAsync(agentKey, "FallbackReply", def.FallbackReply, violations, cancellationToken)
            .ConfigureAwait(false);

        for (int i = 0; i < def.KeywordFastPaths.Count; i++)
        {
            string phrase = def.KeywordFastPaths[i] ?? string.Empty;
            calls += await EvaluateFieldAsync(agentKey, $"KeywordFastPaths[{i}]", phrase, violations, cancellationToken)
                .ConfigureAwait(false);
        }

        return calls;
    }

    private async Task<int> EvaluateFieldAsync(
        string agentKey,
        string field,
        string text,
        List<AgentDefinitionViolation> violations,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var context = new ContentSafetyEvaluationContext(
            UserId: AgentDefinitionDetectionTypes.StartupValidatorContext,
            CheckPromptShield: true);

        ContentSafetyResult result = await _contentSafety
            .EvaluateAsync(text, ContentSafetyStage.AgentDefinition, context, cancellationToken)
            .ConfigureAwait(false);

        switch (result.Decision)
        {
            case ContentSafetyDecision.Passed:
                break;

            case ContentSafetyDecision.Flagged:
                // Flagged is audited but does not block; emit a violation only
                // in the aggregated report so operators can see it, but do not
                // treat it as a hard reject — that mirrors the middleware.
                _logger.LogInformation(
                    "Content Safety flagged agent definition {AgentKey} field {Field} " +
                    "(category={PrimaryCategory}).",
                    agentKey, field, result.PrimaryCategory ?? "n/a");
                break;

            case ContentSafetyDecision.Blocked:
                {
                    string detectionType = ContentSafetyDetectionTypes.ForResultWithShield(result);
                    (string? category, int? severity) = ContentSafetyAuditFields.PickCategoryAndSeverity(result);
                    int? threshold = ContentSafetyAuditFields.ThresholdFor(_guardrails.ContentSafety, category);
                    string primary = result.PrimaryCategory
                        ?? (result.Categories.Count > 0 ? result.Categories[0].Category : "unknown");
                    string message = result.PromptShieldJailbreakDetected
                        ? $"Prompt Shields detected instruction-override / role-reversal in {field}."
                        : result.PromptShieldIndirectInjectionDetected
                            ? $"Prompt Shields detected indirect-injection in {field}."
                            : $"Content Safety blocked {field} on category '{primary}'.";
                    string reason = ContentSafetyAuditFields.BuildReason(
                        result,
                        ContentSafetyStage.AgentDefinition,
                        detectionType,
                        category,
                        severity,
                        threshold);
                    violations.Add(new AgentDefinitionViolation(
                        agentKey, field, "safety.content-safety-blocked",
                        $"{message} {reason}",
                        AgentDefinitionDetectionTypes.ContentSafety,
                        Category: category,
                        Severity: severity,
                        Decision: result.Decision.ToString(),
                        Threshold: threshold));
                    break;
                }

            case ContentSafetyDecision.ServiceUnavailable:
                {
                    if (_guardrails.ContentSafety.OnUnavailable == ContentSafetyFailPolicy.FailClosed)
                    {
                        violations.Add(new AgentDefinitionViolation(
                            agentKey, field, "safety.content-safety-unavailable",
                            $"Content Safety unavailable and FailClosed policy is active; refusing {field}.",
                            AgentDefinitionDetectionTypes.ContentSafety,
                            Decision: result.Decision.ToString()));
                    }
                    else
                    {
                        // Fail-open: pass, but record the pass so operators can
                        // distinguish it from a real allow decision.
                        await _audit.LogAsync(new SuspiciousRequest(
                            Id: Guid.NewGuid().ToString("N"),
                            Timestamp: DateTime.UtcNow,
                            RequestText: Truncate($"Content Safety unavailable while checking {field} for agent {agentKey}."),
                            DetectionType: AgentDefinitionDetectionTypes.ContentSafetyUnavailable,
                            UserContext: AgentDefinitionDetectionTypes.StartupValidatorContext,
                            Action: AgentDefinitionDetectionTypes.ActionFailOpenPassed,
                            Category: null,
                            Severity: null,
                            Decision: ContentSafetyDecision.ServiceUnavailable.ToString(),
                            Stage: ContentSafetyStage.AgentDefinition.ToString(),
                            Threshold: null,
                            Reason: $"Content Safety was unreachable while checking {field} for agent {agentKey}."),
                            cancellationToken).ConfigureAwait(false);
                    }

                    break;
                }

            default:
                break;
        }

        return 1;
    }

    private static void AddDuplicateNameViolations(
        PromptConfiguration config,
        List<AgentDefinitionViolation> violations,
        HashSet<string> offenderKeys)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string sectionKey, Models.AgentDefinition def) in config.Agents)
        {
            string name = def.Name?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                continue;
            }

            if (seen.TryGetValue(name, out string? firstSection))
            {
                offenderKeys.Add(sectionKey);
                violations.Add(new AgentDefinitionViolation(
                    sectionKey, "Name", "structural.duplicate-name",
                    $"Name '{name}' duplicates the definition already declared under section '{firstSection}'.",
                    AgentDefinitionDetectionTypes.Structural));
            }
            else
            {
                seen[name] = sectionKey;
            }
        }
    }

    private Task WriteAuditAsync(
        AgentDefinitionViolation violation,
        string action,
        CancellationToken cancellationToken)
    {
        return _audit.LogAsync(new SuspiciousRequest(
            Id: Guid.NewGuid().ToString("N"),
            Timestamp: DateTime.UtcNow,
            RequestText: Truncate($"agent={violation.AgentKey} field={violation.Field} rule={violation.RuleId}"),
            DetectionType: violation.DetectionType,
            UserContext: AgentDefinitionDetectionTypes.StartupValidatorContext,
            Action: action,
            Category: violation.Category,
            Severity: violation.Severity,
            Decision: violation.Decision,
            Stage: ContentSafetyStage.AgentDefinition.ToString(),
            Threshold: violation.Threshold,
            Reason: violation.Message),
            cancellationToken);
    }

    private static string SummarizeReasons(IReadOnlyList<AgentDefinitionViolation> violations, string agentKey)
    {
        IEnumerable<string> reasons = violations
            .Where(v => string.Equals(v.AgentKey, agentKey, StringComparison.OrdinalIgnoreCase))
            .Select(v => $"{v.Field}:{v.RuleId}");
        return string.Join("; ", reasons);
    }

    private static string Truncate(string value)
    {
        const int max = 200;
        return value.Length <= max ? value : value[..max];
    }
}

/// <summary>
/// Structured outcome of a validator pass. Callers under
/// <see cref="AgentDefinitionFailurePolicy.RefuseStartup"/> never see this —
/// the exception is thrown instead. Under
/// <see cref="AgentDefinitionFailurePolicy.QuarantineOffender"/> the report
/// enumerates removed agent keys so downstream composition can log the trimmed
/// roster.
/// </summary>
public sealed record AgentDefinitionValidationReport(
    IReadOnlyList<AgentDefinitionViolation> Violations,
    IReadOnlyList<string> QuarantinedAgentKeys,
    AgentDefinitionFailurePolicy FailurePolicy);

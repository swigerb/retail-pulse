namespace RetailPulse.Api.Guardrails.AgentDefinition;

/// <summary>
/// Well-known <see cref="Contracts.Guardrails.SuspiciousRequest.DetectionType"/>
/// values emitted by the load-time agent-definition validator (issue #99).
/// Kept separate from the Content Safety detection-type family so the audit
/// dashboard can distinguish deployment-time definition rejections from
/// runtime user-traffic detections without another schema change.
/// </summary>
public static class AgentDefinitionDetectionTypes
{
    /// <summary>Structural rule violation — required field missing, out-of-bounds value, unknown model, etc.</summary>
    public const string Structural = "agent-definition-structural";

    /// <summary>Policy violation — tool references outside the deployment allow-list.</summary>
    public const string Policy = "agent-definition-policy";

    /// <summary>Pattern-layer jailbreak hit in a definition-owned string.</summary>
    public const string Jailbreak = "agent-definition-jailbreak";

    /// <summary>Content Safety second-pass block or Prompt Shields hit.</summary>
    public const string ContentSafety = "agent-definition-content-safety";

    /// <summary>Privileged tool referenced without an explicit deployment grant.</summary>
    public const string PrivilegedGrant = "agent-definition-privileged-grant";

    /// <summary>Content Safety layer unavailable while the fail policy is <c>FailOpen</c>.</summary>
    public const string ContentSafetyUnavailable = "agent-definition-content-safety-unavailable";

    /// <summary>Sentinel user-context value written for every load-time validator audit row.</summary>
    public const string StartupValidatorContext = "startup-validator";

    /// <summary>Audit action written when <see cref="Contracts.Guardrails.AgentDefinitionFailurePolicy.RefuseStartup"/> is active.</summary>
    public const string ActionBlocked = "blocked";

    /// <summary>Audit action written when <see cref="Contracts.Guardrails.AgentDefinitionFailurePolicy.QuarantineOffender"/> is active.</summary>
    public const string ActionQuarantined = "quarantined";

    /// <summary>Audit action written when a Content Safety unavailability is treated as pass under <c>FailOpen</c>.</summary>
    public const string ActionFailOpenPassed = "failopen-passed";
}

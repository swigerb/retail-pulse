namespace RetailPulse.Api.Guardrails.AgentDefinition;

/// <summary>
/// A single rule violation produced by <c>AgentDefinitionValidator</c>. The
/// record is the shared row shape between the aggregated exception message
/// and the audit row written to <c>ISuspiciousRequestLog</c>. It never carries
/// raw payload text — <see cref="Message"/> is operator-actionable diagnostic
/// prose only.
/// </summary>
public sealed record AgentDefinitionViolation(
    string AgentKey,
    string Field,
    string RuleId,
    string Message,
    string DetectionType,
    string? Category = null,
    int? Severity = null,
    string? Decision = null,
    int? Threshold = null);

namespace RetailPulse.Api.Guardrails.AgentDefinition;

/// <summary>
/// Thrown by <c>AgentDefinitionValidator</c> when
/// <see cref="Contracts.Guardrails.AgentDefinitionFailurePolicy.RefuseStartup"/>
/// is active and one or more definitions failed validation. Mirrors the shape
/// of <c>UnknownToolReferenceException</c> — a single summary line followed by
/// one bullet per offender — so the host exits non-zero with a diagnosable
/// message.
/// </summary>
/// <remarks>
/// The exception carries the full list of violations so an operator sees
/// every offender in one shot. Raw prompt content is deliberately excluded —
/// audit rows with a correlation id are the source of forensic detail.
/// </remarks>
public sealed class AgentDefinitionValidationException : InvalidOperationException
{
    public IReadOnlyList<AgentDefinitionViolation> Violations { get; }

    public AgentDefinitionValidationException(IReadOnlyList<AgentDefinitionViolation> violations)
        : base(BuildMessage(violations))
    {
        ArgumentNullException.ThrowIfNull(violations);
        Violations = violations;
    }

    private static string BuildMessage(IReadOnlyList<AgentDefinitionViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);
        if (violations.Count == 0)
        {
            return "Agent definition validation failed with no violations recorded — this indicates a validator bug.";
        }

        var lines = new List<string>(violations.Count + 1)
        {
            $"Agent definition validation failed for {violations.Count} rule violation(s). " +
            "Configuration errors must fail at startup, not at first user query. " +
            "See the /api/guardrails/log audit feed for correlation identifiers:",
        };

        foreach (AgentDefinitionViolation v in violations
            .OrderBy(v => v.AgentKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Field, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.RuleId, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"  - agent='{v.AgentKey}' field='{v.Field}' rule='{v.RuleId}': {v.Message}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

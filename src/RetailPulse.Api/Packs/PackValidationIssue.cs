namespace RetailPulse.Api.Packs;

/// <summary>
/// One aggregated finding produced by <see cref="PackLoader"/>. Every
/// issue names the offending pack and the section (or file, or agent
/// key) so operators reading the aggregate error can jump straight to
/// the source rather than reverse-engineering which pack file failed.
/// </summary>
/// <param name="PackName">Directory name of the offending pack — for
/// example <c>default</c> or <c>halcyon-pet-supply</c>.</param>
/// <param name="Section">Where inside the pack the issue was detected.
/// Uses stable, human-readable labels: <c>pack.yaml</c>,
/// <c>agents.yaml</c>, <c>starting-tasks.yaml</c>,
/// <c>knowledge/{filename}</c>, or <c>agents.yaml#{agentKey}</c>.
/// </param>
/// <param name="Message">Actionable diagnostic. Includes the offending
/// value where relevant, and the corrective action.</param>
/// <param name="Code">Machine-readable code (e.g.,
/// <c>pack.missing</c>) so the loader can be exercised in a table-driven
/// test without pinning brittle message strings.</param>
public sealed record PackValidationIssue(
    string PackName,
    string Section,
    string Message,
    string Code)
{
    /// <summary>Formatted string for logging and exception messages.</summary>
    public override string ToString() =>
        $"[pack '{PackName}' → {Section}] {Message}";
}

namespace RetailPulse.Api.Packs;

/// <summary>
/// Raised by <see cref="PackLoader"/> when a pack fails validation.
/// Aggregates every finding discovered across the pack so operators
/// see the full failure surface, not just the first blocking issue.
/// </summary>
public sealed class PackValidationException : Exception
{
    public IReadOnlyList<PackValidationIssue> Issues { get; }

    public PackValidationException(string packName, IReadOnlyList<PackValidationIssue> issues)
        : base(BuildMessage(packName, issues))
    {
        ArgumentNullException.ThrowIfNull(issues);
        Issues = issues;
    }

    private static string BuildMessage(string packName, IReadOnlyList<PackValidationIssue> issues)
    {
        if (issues.Count == 0)
        {
            return $"Pack '{packName}' failed validation with no issues recorded.";
        }

        string header = $"Pack '{packName}' failed validation with {issues.Count} issue(s):";
        string body = string.Join(
            Environment.NewLine,
            issues.Select(i => "  - " + i.ToString()));
        return header + Environment.NewLine + body;
    }
}

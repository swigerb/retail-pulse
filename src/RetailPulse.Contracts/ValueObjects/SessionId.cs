using System.Text.RegularExpressions;

namespace RetailPulse.Contracts.ValueObjects;

/// <summary>
/// Value object representing a session identifier. Validates alphanumeric + hyphens format.
/// </summary>
public readonly partial record struct SessionId
{
    private static readonly Regex ValidPattern = CreatePattern();

    public string Value { get; }

    public SessionId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(value));
        var trimmed = value.Trim();
        if (!ValidPattern.IsMatch(trimmed))
        {
            throw new ArgumentException(
                $"SessionId must be alphanumeric with hyphens only. Got: '{trimmed}'",
                nameof(value));
        }
        Value = trimmed;
    }

    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && ValidPattern.IsMatch(value.Trim());

    public static implicit operator SessionId(string value) => new(value);
    public static implicit operator string(SessionId sessionId) => sessionId.Value;

    public override string ToString() => Value;

    [GeneratedRegex(@"^[a-zA-Z0-9\-]+$", RegexOptions.Compiled)]
    private static partial Regex CreatePattern();
}

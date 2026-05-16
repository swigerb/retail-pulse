namespace RetailPulse.Api.Guardrails;

/// <summary>
/// Pattern-matching jailbreak detector. Case-insensitive substring matching
/// against a configurable set of jailbreak patterns.
/// </summary>
public class JailbreakDetector
{
    private readonly string[] _patterns;

    /// <summary>Default jailbreak patterns when no config is provided.</summary>
    public static readonly string[] DefaultPatterns =
    [
        "ignore previous instructions",
        "ignore all previous",
        "disregard previous",
        "forget your instructions",
        "you are now",
        "pretend you are",
        "act as if you",
        "system prompt override",
        "override system prompt",
        "new persona",
        "ignore your programming",
        "bypass your filters",
        "ignore safety",
        "ignore content policy",
        "jailbreak",
        "DAN mode",
        "developer mode enabled"
    ];

    public JailbreakDetector(JailbreakConfig? config = null)
    {
        _patterns = config?.Patterns ?? DefaultPatterns;
    }

    /// <summary>
    /// Returns true if the input text matches any known jailbreak pattern.
    /// </summary>
    public bool IsJailbreakAttempt(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        string lower = input.ToLowerInvariant();
        return _patterns.Any(p => lower.Contains(p.ToLowerInvariant()));
    }

    /// <summary>
    /// Returns the matched pattern, or null if no match.
    /// </summary>
    public string? GetMatchedPattern(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        string lower = input.ToLowerInvariant();
        return _patterns.FirstOrDefault(p => lower.Contains(p.ToLowerInvariant()));
    }
}

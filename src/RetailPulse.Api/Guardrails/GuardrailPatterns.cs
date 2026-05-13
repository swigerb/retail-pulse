using System.Text.RegularExpressions;

namespace RetailPulse.Api.Guardrails;

/// <summary>
/// Static pattern library for PII and jailbreak detection.
/// All patterns are compiled at class-load time for fast matching.
/// </summary>
public static partial class GuardrailPatterns
{
    // ── PII patterns ─────────────────────────────────────────────────────

    /// <summary>SSN: 123-45-6789</summary>
    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled)]
    public static partial Regex SsnPattern();

    /// <summary>Email address</summary>
    [GeneratedRegex(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled)]
    public static partial Regex EmailPattern();

    /// <summary>US phone number with optional +1 prefix</summary>
    [GeneratedRegex(@"\b(\+1[-.]?)?\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}\b", RegexOptions.Compiled)]
    public static partial Regex PhonePattern();

    /// <summary>Credit card number (16 digits, optional separators)</summary>
    [GeneratedRegex(@"\b\d{4}[-\s]?\d{4}[-\s]?\d{4}[-\s]?\d{4}\b", RegexOptions.Compiled)]
    public static partial Regex CreditCardPattern();

    /// <summary>All PII patterns for batch scanning.</summary>
    public static readonly (string Name, Regex Pattern)[] PiiPatterns =
    [
        ("SSN", SsnPattern()),
        ("Email", EmailPattern()),
        ("Phone", PhonePattern()),
        ("CreditCard", CreditCardPattern())
    ];

    // ── Jailbreak patterns ───────────────────────────────────────────────

    [GeneratedRegex(@"ignore\s+(all\s+)?(previous\s+|prior\s+)?instructions", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    public static partial Regex JailbreakIgnoreInstructions();

    [GeneratedRegex(@"you are now\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    public static partial Regex JailbreakYouAreNow();

    [GeneratedRegex(@"pretend\s+(to be|you'?re)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    public static partial Regex JailbreakPretend();

    [GeneratedRegex(@"\bsystem prompt\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    public static partial Regex JailbreakSystemPrompt();

    [GeneratedRegex(@"reveal your\s+(instructions|prompt|rules)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    public static partial Regex JailbreakReveal();

    [GeneratedRegex(@"disregard\s+(all\s+|any\s+)?(rules|guidelines|instructions)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    public static partial Regex JailbreakDisregard();

    /// <summary>All jailbreak patterns for batch scanning.</summary>
    public static readonly (string Name, Regex Pattern)[] JailbreakPatterns =
    [
        ("ignore_instructions", JailbreakIgnoreInstructions()),
        ("you_are_now", JailbreakYouAreNow()),
        ("pretend", JailbreakPretend()),
        ("system_prompt", JailbreakSystemPrompt()),
        ("reveal", JailbreakReveal()),
        ("disregard", JailbreakDisregard())
    ];

    /// <summary>
    /// Scans input for PII. Returns all detected PII types, or empty if clean.
    /// </summary>
    public static IReadOnlyList<string> DetectPii(string input)
    {
        var detections = new List<string>();
        foreach (var (name, pattern) in PiiPatterns)
        {
            if (pattern.IsMatch(input))
                detections.Add(name);
        }
        return detections;
    }

    /// <summary>
    /// Scans input for jailbreak attempts. Returns all matched pattern names, or empty if clean.
    /// </summary>
    public static IReadOnlyList<string> DetectJailbreak(string input)
    {
        var detections = new List<string>();
        foreach (var (name, pattern) in JailbreakPatterns)
        {
            if (pattern.IsMatch(input))
                detections.Add(name);
        }
        return detections;
    }
}

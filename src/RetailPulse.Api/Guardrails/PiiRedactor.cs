using System.Text.RegularExpressions;

namespace RetailPulse.Api.Guardrails;

/// <summary>
/// Regex-based PII detection and redaction.
/// Replaces SSNs, emails, phone numbers, and credit card numbers with typed placeholders.
/// </summary>
public static partial class PiiRedactor
{
    /// <summary>
    /// Redacts all recognized PII patterns from the input text.
    /// </summary>
    public static string Redact(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        string result = input;
        result = SsnPattern().Replace(result, "[REDACTED:ssn]");
        result = CreditCardPattern().Replace(result, "[REDACTED:credit_card]");
        result = EmailPattern().Replace(result, "[REDACTED:email]");
        result = PhonePattern().Replace(result, "[REDACTED:phone]");
        return result;
    }

    /// <summary>
    /// Returns true if any PII is detected in the input.
    /// </summary>
    public static bool ContainsPii(string input)
    {
        return !string.IsNullOrEmpty(input) && (SsnPattern().IsMatch(input)
            || EmailPattern().IsMatch(input)
            || PhonePattern().IsMatch(input)
            || CreditCardPattern().IsMatch(input));
    }

    // SSN: 123-45-6789 or 123 45 6789
    [GeneratedRegex(@"\b\d{3}[-\s]\d{2}[-\s]\d{4}\b", RegexOptions.Compiled)]
    private static partial Regex SsnPattern();

    // Credit cards: 4 groups of 4 digits (space or dash separated) or 16 contiguous digits
    [GeneratedRegex(@"\b\d{4}[\s-]\d{4}[\s-]\d{4}[\s-]\d{4}\b|\b\d{16}\b", RegexOptions.Compiled)]
    private static partial Regex CreditCardPattern();

    // Email
    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled)]
    private static partial Regex EmailPattern();

    // Phone: (555) 123-4567 or 555-123-4567 or 555.123.4567
    [GeneratedRegex(@"(\(\d{3}\)\s?\d{3}[-.\s]\d{4}|\b\d{3}[-.\s]\d{3}[-.\s]\d{4}\b)", RegexOptions.Compiled)]
    private static partial Regex PhonePattern();
}

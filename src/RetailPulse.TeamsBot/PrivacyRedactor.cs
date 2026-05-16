using System.Security.Cryptography;
using System.Text;

namespace RetailPulse.TeamsBot;

/// <summary>
/// Helpers for keeping PII out of bot logs. User-supplied content is never logged verbatim;
/// instead we emit a length, an irreversibly-hashed identifier, or just the email domain.
/// </summary>
internal static class PrivacyRedactor
{
    /// <summary>
    /// Returns a short placeholder describing a user message without exposing its contents.
    /// Example: "[user message - 42 chars]".
    /// </summary>
    public static string DescribeMessage(string? message)
    {
        int length = message?.Length ?? 0;
        return $"[user message - {length} chars]";
    }

    /// <summary>
    /// Returns "domain" for "name@domain" and a stable short hash for any local part, so logs
    /// can correlate activity across a single user without revealing the email itself.
    /// Example: "alice@contoso.com" -> "u:9f2a@contoso.com".
    /// </summary>
    public static string RedactEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return "[no-email]";
        }

        int atIndex = email.IndexOf('@');
        if (atIndex <= 0 || atIndex == email.Length - 1)
        {
            // Not a recognizable email; emit a hash only.
            return $"u:{ShortHash(email)}";
        }

        string local = email[..atIndex];
        string domain = email[(atIndex + 1)..];
        return $"u:{ShortHash(local)}@{domain}";
    }

    /// <summary>
    /// Returns "[name]" — display names are user-supplied and may include PII; we replace
    /// with a stable short hash so logs can still correlate per-user activity.
    /// </summary>
    public static string RedactName(string? name) => string.IsNullOrWhiteSpace(name) ? "[no-name]" : $"u:{ShortHash(name)}";

    private static string ShortHash(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        // 4 bytes (8 hex chars) is plenty for log correlation and avoids reversibility.
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }
}

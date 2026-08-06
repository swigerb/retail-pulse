using System.Text;
using Microsoft.Extensions.Hosting;

namespace RetailPulse.Api.Security.Anonymous;

/// <summary>
/// Resolved, validated configuration for the Anonymous authentication mode.
///
/// Anonymous mode reaches billable models, so it is fail-closed by construction:
/// <list type="bullet">
///   <item>It only runs when <c>Authentication:Mode=Anonymous</c> is set explicitly (resolved by
///     <see cref="AuthenticationModeOptions"/>; no auto-detection).</item>
///   <item>Any hosted (non-Development) deployment additionally requires a SECOND explicit opt-in,
///     <c>Anonymous:AllowHosted=true</c>, PLUS a complete, validated guardrail configuration —
///     a strong signing key and positive daily request/token/cost ceilings. A missing, malformed,
///     or unsafe value throws at startup so a misconfigured hosted deploy never serves traffic.</item>
///   <item>Development may run without <c>AllowHosted</c> and without a configured signing key
///     (an ephemeral process-local key is generated — sessions die on restart).</item>
/// </list>
/// The signing key is a SECRET and is never committed; it is supplied at runtime via
/// <c>Anonymous__SigningKey</c> or a secret store.
/// </summary>
public sealed class AnonymousAuthOptions
{
    public const string SectionName = "Anonymous";

    // ── Session token ────────────────────────────────────────────────────────
    public string Issuer { get; init; } = "retail-pulse-anonymous";
    public string Audience { get; init; } = "retail-pulse-api";

    /// <summary>Short-lived session token TTL. Bounded to keep the replay window small.</summary>
    public int SessionTokenTtlSeconds { get; init; } = 900;

    /// <summary>HMAC signing key (secret). Required in hosted mode; ephemeral in Development.</summary>
    public string? SigningKey { get; init; }

    public string Role { get; init; } = AnonymousCapabilityPolicy.DefaultRole;
    public string Scope { get; init; } = AnonymousCapabilityPolicy.DefaultScope;

    /// <summary>Hard cap on model output tokens for anonymous chat.</summary>
    public int MaxOutputTokens { get; init; } = 512;

    /// <summary>Max accepted request body size (bytes) for anonymous callers.</summary>
    public int MaxRequestBytes { get; init; } = 16 * 1024;

    /// <summary>Per-request pipeline timeout (seconds) for anonymous callers.</summary>
    public int RequestTimeoutSeconds { get; init; } = 60;

    // ── Rate limits ──────────────────────────────────────────────────────────
    public int BootstrapPerIpPerMinute { get; init; } = 5;
    public int ChatPerSubjectPerMinute { get; init; } = 5;
    public int ChatPerIpPerMinute { get; init; } = 10;

    // ── Hosted global daily ceilings (billable-use circuit breaker) ───────────
    public int DailyMaxRequests { get; init; } = 500;
    public long DailyMaxTokens { get; init; } = 200_000;
    public decimal DailyMaxCostUsd { get; init; } = 5.00m;

    /// <summary>True once the hosted opt-in and guardrails have been validated and enforced.</summary>
    public bool AllowHosted { get; init; }

    /// <summary>
    /// True when this process must enforce the hosted guardrails (hosted deploy, or an explicit
    /// <c>AllowHosted=true</c> even in Development). Drives the circuit breaker and replica pinning.
    /// </summary>
    public bool HostedGuardrailsEnforced { get; init; }

    /// <summary>True when the signing key was supplied via configuration (not ephemerally generated).</summary>
    public bool HasConfiguredSigningKey => !string.IsNullOrWhiteSpace(SigningKey) && !IsPlaceholder(SigningKey);

    /// <summary>
    /// Resolves and validates options from configuration. Throws at startup for any missing,
    /// malformed, or unsafe value that would make a hosted Anonymous deployment unsafe.
    /// </summary>
    public static AnonymousAuthOptions FromConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        IConfigurationSection section = configuration.GetSection(SectionName);
        bool isDevelopment = environment.IsDevelopment();
        bool allowHosted = section.GetValue("AllowHosted", false);

        // Any non-Development environment is "hosted" and MUST carry the second explicit opt-in.
        bool hosted = !isDevelopment;
        if (hosted && !allowHosted)
        {
            throw new InvalidOperationException(
                $"Anonymous authentication is enabled in the '{environment.EnvironmentName}' environment but " +
                "Anonymous:AllowHosted is not true. A hosted/non-Development Anonymous deployment requires a " +
                "SECOND explicit opt-in (Anonymous__AllowHosted=true) plus complete guardrail configuration. " +
                "This fails closed by design — Anonymous mode reaches billable models.");
        }

        // Enforce the full guardrail set whenever hosting is in play (a hosted deploy, or an
        // explicit AllowHosted=true even locally). Development without AllowHosted stays lenient.
        bool enforce = hosted || allowHosted;

        var options = new AnonymousAuthOptions
        {
            Issuer = Clean(section["Issuer"]) ?? "retail-pulse-anonymous",
            Audience = Clean(section["Audience"]) ?? "retail-pulse-api",
            SessionTokenTtlSeconds = section.GetValue("SessionTokenTtlSeconds", 900),
            SigningKey = Clean(section["SigningKey"]),
            Role = Clean(section["Role"]) ?? AnonymousCapabilityPolicy.DefaultRole,
            Scope = Clean(section["Scope"]) ?? AnonymousCapabilityPolicy.DefaultScope,
            MaxOutputTokens = section.GetValue("MaxOutputTokens", 512),
            MaxRequestBytes = section.GetValue("MaxRequestBytes", 16 * 1024),
            RequestTimeoutSeconds = section.GetValue("RequestTimeoutSeconds", 60),
            BootstrapPerIpPerMinute = section.GetValue("Bootstrap:PerIpPerMinute", 5),
            ChatPerSubjectPerMinute = section.GetValue("Chat:PerSubjectPerMinute", 5),
            ChatPerIpPerMinute = section.GetValue("Chat:PerIpPerMinute", 10),
            DailyMaxRequests = section.GetValue("Limits:DailyMaxRequests", 500),
            DailyMaxTokens = section.GetValue("Limits:DailyMaxTokens", 200_000L),
            DailyMaxCostUsd = section.GetValue("Limits:DailyMaxCostUsd", 5.00m),
            AllowHosted = allowHosted,
            HostedGuardrailsEnforced = enforce,
        };

        options.Validate(enforce, environment.EnvironmentName);
        return options;
    }

    private void Validate(bool enforce, string environmentName)
    {
        RequireInRange(SessionTokenTtlSeconds, 30, 3600, "Anonymous:SessionTokenTtlSeconds");
        RequireInRange(MaxOutputTokens, 1, 4096, "Anonymous:MaxOutputTokens");
        RequireInRange(MaxRequestBytes, 256, 1_000_000, "Anonymous:MaxRequestBytes");
        RequireInRange(RequestTimeoutSeconds, 1, 120, "Anonymous:RequestTimeoutSeconds");
        RequirePositive(BootstrapPerIpPerMinute, "Anonymous:Bootstrap:PerIpPerMinute");
        RequirePositive(ChatPerSubjectPerMinute, "Anonymous:Chat:PerSubjectPerMinute");
        RequirePositive(ChatPerIpPerMinute, "Anonymous:Chat:PerIpPerMinute");

        if (string.IsNullOrWhiteSpace(Issuer) || string.IsNullOrWhiteSpace(Audience))
        {
            throw new InvalidOperationException(
                "Anonymous:Issuer and Anonymous:Audience are required so session tokens can be validated.");
        }

        if (!enforce)
        {
            return; // Development without AllowHosted: ephemeral key + lenient ceilings are allowed.
        }

        // Hosted guardrails — every one of these is mandatory and must be safe.
        if (!HasConfiguredSigningKey)
        {
            throw new InvalidOperationException(
                $"Anonymous:SigningKey is required for a hosted Anonymous deployment ('{environmentName}'). " +
                "Supply a strong secret via Anonymous__SigningKey or a secret store — never commit it. " +
                "An ephemeral key is only permitted in Development.");
        }

        if (Encoding.UTF8.GetByteCount(SigningKey!) < 32)
        {
            throw new InvalidOperationException(
                "Anonymous:SigningKey must be at least 32 bytes (256-bit) for HMAC-SHA256 signing.");
        }

        RequirePositive(DailyMaxRequests, "Anonymous:Limits:DailyMaxRequests");
        RequirePositive(DailyMaxTokens, "Anonymous:Limits:DailyMaxTokens");
        if (DailyMaxCostUsd <= 0m)
        {
            throw new InvalidOperationException(
                "Anonymous:Limits:DailyMaxCostUsd must be greater than zero for a hosted Anonymous deployment. " +
                "Missing or non-positive billable-use ceilings fail closed.");
        }
    }

    private static void RequirePositive(long value, string name)
    {
        if (value <= 0)
        {
            throw new InvalidOperationException($"{name} must be greater than zero (was {value}).");
        }
    }

    private static void RequireInRange(int value, int min, int max, string name)
    {
        if (value < min || value > max)
        {
            throw new InvalidOperationException($"{name} must be between {min} and {max} (was {value}).");
        }
    }

    private static bool IsPlaceholder(string? value) =>
        !string.IsNullOrWhiteSpace(value) && (value.Contains('<') || value.Contains('>'));

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return IsPlaceholder(trimmed) ? null : trimmed;
    }
}

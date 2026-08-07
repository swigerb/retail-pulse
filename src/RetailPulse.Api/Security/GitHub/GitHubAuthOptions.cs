using System.Text;
using Microsoft.Extensions.Hosting;

namespace RetailPulse.Api.Security.GitHub;

/// <summary>
/// Resolved, validated configuration for the GitHub authentication mode.
///
/// GitHub mode is a confidential backend-for-frontend (BFF) OAuth flow that ultimately mints a
/// billable-capable, full <c>RetailPulse.User</c> session, so it is fail-closed by construction:
/// <list type="bullet">
///   <item>It only runs when <c>Authentication:Mode=GitHub</c> is set explicitly (resolved by
///     <see cref="AuthenticationModeOptions"/>; no auto-detection).</item>
///   <item>The GitHub OAuth <b>client id</b> is public configuration; the <b>client secret</b> and the
///     <b>app session signing key</b> are SECRETS supplied at runtime (<c>GitHub__ClientSecret</c> /
///     <c>GitHub__SigningKey</c> or a secret store) and are never committed, logged, or emitted.</item>
///   <item>Any hosted (non-Development) deployment requires a COMPLETE, validated configuration —
///     client id + client secret + a ≥ 256-bit signing key + the exact API callback URL + the exact
///     frontend return URL + at least one allowlist mechanism. A missing, malformed, or placeholder
///     value throws at startup so a misconfigured hosted deploy never serves traffic.</item>
///   <item>Development may run the same real OAuth flow with an ephemeral process-local signing key
///     (sessions die on restart) but STILL requires the real OAuth app credentials, exact URLs, and
///     an allowlist — there is no "allow everyone" shortcut.</item>
/// </list>
/// Angle-bracket placeholder values (e.g. <c>&lt;set-via-secret-store&gt;</c>) from the example config
/// are treated as absent and rejected, so a copied template can never accidentally authenticate.
/// </summary>
public sealed class GitHubAuthOptions
{
    public const string SectionName = "GitHub";

    // ── OAuth app (confidential client) ──────────────────────────────────────
    /// <summary>GitHub OAuth app client id. PUBLIC configuration (appears in the authorize URL).</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>GitHub OAuth app client secret. SECRET — never committed/logged; required hosted.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>
    /// The EXACT OAuth callback URL registered with the GitHub app and sent as <c>redirect_uri</c>
    /// on both the authorize request and the token exchange. Must be the API's own
    /// <see cref="GitHubAuthConstants.CallbackRoute"/> endpoint over HTTPS.
    /// </summary>
    public string CallbackUrl { get; init; } = string.Empty;

    /// <summary>
    /// The ONE exact SPA/SWA URL the browser is redirected to after a successful login, carrying only
    /// a short-lived one-time redemption code (never a provider or app token). No user-supplied
    /// redirect target is ever honoured — this closes the open-redirect class.
    /// </summary>
    public string FrontendReturnUrl { get; init; } = string.Empty;

    // ── App session token ────────────────────────────────────────────────────
    public string Issuer { get; init; } = "retail-pulse-github";
    public string Audience { get; init; } = "retail-pulse-api";

    /// <summary>Short-lived session token TTL. Bounded to keep the replay window small. No refresh.</summary>
    public int SessionTokenTtlSeconds { get; init; } = 900;

    /// <summary>HMAC signing key (secret). Required in hosted mode; ephemeral in Development.</summary>
    public string? SigningKey { get; init; }

    public string Role { get; init; } = GitHubAuthConstants.DefaultRole;
    public string Scope { get; init; } = GitHubAuthConstants.DefaultScope;

    // ── Server-side one-time stores (TTL) ────────────────────────────────────
    /// <summary>How long an issued OAuth state entry is valid (login must complete within this).</summary>
    public int StateTtlSeconds { get; init; } = 300;

    /// <summary>How long the one-time redemption code is valid before the SPA must exchange it.</summary>
    public int RedemptionTtlSeconds { get; init; } = 120;

    // ── Allowlist (server-side, immutable numeric id / login / org membership) ─
    /// <summary>Immutable numeric GitHub user ids explicitly allowed.</summary>
    public IReadOnlyList<long> AllowedUserIds { get; init; } = [];

    /// <summary>Configurable login-handle allowlist (case-insensitive). Convenience only — the numeric
    /// id remains the identity; a matched login is still keyed to its immutable id.</summary>
    public IReadOnlyList<string> AllowedLogins { get; init; } = [];

    /// <summary>Organizations whose ACTIVE members are allowed (verified via
    /// <c>/user/memberships/orgs/{org}</c>; requires the <c>read:org</c> scope).</summary>
    public IReadOnlyList<string> AllowedOrgs { get; init; } = [];

    // ── Rate limits ──────────────────────────────────────────────────────────
    public int StartPerMinute { get; init; } = 10;
    public int ExchangePerMinute { get; init; } = 20;

    /// <summary>True when a strong signing key was configured (not ephemerally generated).</summary>
    public bool HasConfiguredSigningKey => IsPresent(SigningKey);

    /// <summary>
    /// The OAuth scopes requested at authorize time. Deliberately MINIMAL and never includes
    /// <c>repo</c> or any write scope: an empty scope suffices to read the numeric id + login from
    /// <c>/user</c>; <c>read:org</c> is added ONLY when an org allowlist is configured (required to
    /// read org membership). Returned as a single space-delimited string for the authorize URL.
    /// </summary>
    public string RequestedScopes => AllowedOrgs.Count > 0 ? "read:org" : string.Empty;

    /// <summary>
    /// Resolves and validates options from configuration. Throws at startup for any missing,
    /// malformed, or placeholder value that would make a GitHub deployment unsafe or non-functional.
    /// </summary>
    public static GitHubAuthOptions FromConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        IConfigurationSection section = configuration.GetSection(SectionName);
        bool enforceSecret = !environment.IsDevelopment();

        var options = new GitHubAuthOptions
        {
            ClientId = Clean(section["ClientId"]) ?? string.Empty,
            ClientSecret = Clean(section["ClientSecret"]),
            CallbackUrl = Clean(section["CallbackUrl"]) ?? string.Empty,
            FrontendReturnUrl = Clean(section["FrontendReturnUrl"]) ?? string.Empty,
            Issuer = Clean(section["Issuer"]) ?? "retail-pulse-github",
            Audience = Clean(section["Audience"]) ?? "retail-pulse-api",
            SessionTokenTtlSeconds = section.GetValue("SessionTokenTtlSeconds", 900),
            SigningKey = Clean(section["SigningKey"]),
            Role = Clean(section["Role"]) ?? GitHubAuthConstants.DefaultRole,
            Scope = Clean(section["Scope"]) ?? GitHubAuthConstants.DefaultScope,
            StateTtlSeconds = section.GetValue("StateTtlSeconds", 300),
            RedemptionTtlSeconds = section.GetValue("RedemptionTtlSeconds", 120),
            AllowedUserIds = ParseUserIds(section.GetSection("AllowedUserIds").Get<string[]>()),
            AllowedLogins = CleanList(section.GetSection("AllowedLogins").Get<string[]>()),
            AllowedOrgs = CleanList(section.GetSection("AllowedOrgs").Get<string[]>()),
            StartPerMinute = section.GetValue("RateLimits:StartPerMinute", 10),
            ExchangePerMinute = section.GetValue("RateLimits:ExchangePerMinute", 20),
        };

        options.Validate(enforceSecret, environment.EnvironmentName);
        return options;
    }

    private void Validate(bool enforceSecret, string environmentName)
    {
        RequireInRange(SessionTokenTtlSeconds, 30, 3600, "GitHub:SessionTokenTtlSeconds");
        RequireInRange(StateTtlSeconds, 30, 1800, "GitHub:StateTtlSeconds");
        RequireInRange(RedemptionTtlSeconds, 10, 600, "GitHub:RedemptionTtlSeconds");
        RequirePositive(StartPerMinute, "GitHub:RateLimits:StartPerMinute");
        RequirePositive(ExchangePerMinute, "GitHub:RateLimits:ExchangePerMinute");

        if (string.IsNullOrWhiteSpace(Issuer) || string.IsNullOrWhiteSpace(Audience))
        {
            throw new InvalidOperationException(
                "GitHub:Issuer and GitHub:Audience are required so session tokens can be validated.");
        }

        // The OAuth app client id is always required — the flow cannot start without it.
        if (!IsPresent(ClientId))
        {
            throw new InvalidOperationException(
                "GitHub:ClientId is required to run GitHub authentication mode. It is the public OAuth " +
                "app client id (not a secret). A missing or placeholder value fails closed.");
        }

        // Exact, HTTPS, fixed redirect URLs — no user-supplied redirect is ever honoured, closing the
        // open-redirect class. The callback must be the API's own callback route.
        RequireHttpsUrl(CallbackUrl, "GitHub:CallbackUrl");
        RequireHttpsUrl(FrontendReturnUrl, "GitHub:FrontendReturnUrl");
        if (!CallbackUrl.EndsWith(GitHubAuthConstants.CallbackRoute, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"GitHub:CallbackUrl must be the API's own OAuth callback endpoint ending in " +
                $"'{GitHubAuthConstants.CallbackRoute}' and match the URL registered with the GitHub OAuth app.");
        }

        // Fail closed on an empty allowlist: GitHub mode must NEVER admit every GitHub account. At
        // least one of numeric id / login / org membership must be configured.
        if (AllowedUserIds.Count == 0 && AllowedLogins.Count == 0 && AllowedOrgs.Count == 0)
        {
            throw new InvalidOperationException(
                "GitHub mode requires an explicit allowlist — configure at least one of " +
                "GitHub:AllowedUserIds, GitHub:AllowedLogins, or GitHub:AllowedOrgs. An empty allowlist " +
                "would admit every GitHub user and fails closed.");
        }

        // The client secret and signing key are secrets. Required in any hosted environment; in
        // Development the secret is still required to talk to GitHub, but the signing key may be
        // ephemeral (process-local; sessions die on restart).
        if (!IsPresent(ClientSecret))
        {
            throw new InvalidOperationException(
                $"GitHub:ClientSecret is required ('{environmentName}'). Supply the OAuth app client " +
                "secret via GitHub__ClientSecret or a secret store — never commit it. A missing or " +
                "placeholder value fails closed.");
        }

        if (enforceSecret)
        {
            if (!HasConfiguredSigningKey)
            {
                throw new InvalidOperationException(
                    $"GitHub:SigningKey is required for a hosted GitHub deployment ('{environmentName}'). " +
                    "Supply a strong secret via GitHub__SigningKey or a secret store — never commit it. " +
                    "An ephemeral key is only permitted in Development.");
            }

            if (Encoding.UTF8.GetByteCount(SigningKey!) < 32)
            {
                throw new InvalidOperationException(
                    "GitHub:SigningKey must be at least 32 bytes (256-bit) for HMAC-SHA256 signing.");
            }
        }
        else if (HasConfiguredSigningKey && Encoding.UTF8.GetByteCount(SigningKey!) < 32)
        {
            // If a key IS supplied in Development it must still be strong (no weak keys anywhere).
            throw new InvalidOperationException(
                "GitHub:SigningKey must be at least 32 bytes (256-bit) when configured.");
        }
    }

    private static IReadOnlyList<long> ParseUserIds(string[]? raw)
    {
        if (raw is null)
        {
            return [];
        }

        var ids = new List<long>();
        foreach (string entry in raw)
        {
            string? cleaned = Clean(entry);
            if (cleaned is null)
            {
                continue;
            }

            if (!long.TryParse(cleaned, out long id) || id <= 0)
            {
                throw new InvalidOperationException(
                    $"GitHub:AllowedUserIds contains a non-numeric or non-positive value '{entry}'. " +
                    "GitHub user ids are positive integers.");
            }

            ids.Add(id);
        }

        return ids;
    }

    private static IReadOnlyList<string> CleanList(string[]? raw)
    {
        return raw is null
            ? []
            : [.. raw.Select(Clean).Where(v => v is not null).Select(v => v!).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static void RequireHttpsUrl(string value, string name)
    {
        if (!IsPresent(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"{name} must be a fixed, absolute HTTPS URL. A missing, non-absolute, non-HTTPS, or " +
                "placeholder value fails closed.");
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

    /// <summary>A value is "present" only when it is non-blank AND not an angle-bracket placeholder.</summary>
    private static bool IsPresent(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Contains('<') && !value.Contains('>');

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Contains('<') || trimmed.Contains('>') ? null : trimmed;
    }
}

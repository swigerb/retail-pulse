using Microsoft.Extensions.Hosting;

namespace RetailPulse.Api.Security;

/// <summary>
/// Resolved, tenant-scoped Microsoft Entra authentication configuration for the API.
///
/// Values are read from the <c>MicrosoftEntra</c> configuration section (aligned with
/// the Teams bot's existing <c>MicrosoftEntra:TenantId/ClientId</c> convention), with a
/// backward-compatible fallback to the legacy <c>Security:JwtAuthority/JwtAudience</c>
/// keys. Tenant ID and client ID are configuration, NOT secrets — this app never uses a
/// client secret (SPA/API use OAuth authorization code + PKCE).
/// </summary>
public sealed class EntraAuthOptions
{
    public const string SectionName = "MicrosoftEntra";

    /// <summary>Default delegated API scope exposed by the API app registration.</summary>
    public const string DefaultApiScope = "access_as_user";

    /// <summary>Default app role that assigned users must hold to call the API.</summary>
    public const string DefaultAppRole = "RetailPulse.User";

    /// <summary>Cloud instance base, e.g. https://login.microsoftonline.com/.</summary>
    public string Instance { get; init; } = "https://login.microsoftonline.com/";

    /// <summary>Single tenant (directory) ID the API is pinned to.</summary>
    public string? TenantId { get; init; }

    /// <summary>Application (client) ID of the SPA/API registration — this is the token audience.</summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// Explicit audience override. When empty the audience defaults to both
    /// <c>ClientId</c> and <c>api://{ClientId}</c> (v1 and v2 token audiences).
    /// </summary>
    public string? Audience { get; init; }

    /// <summary>Delegated scope name the SPA must request and the token must carry (scp claim).</summary>
    public string ApiScope { get; init; } = DefaultApiScope;

    /// <summary>App role name required on every protected endpoint/hub (roles claim).</summary>
    public string AppRole { get; init; } = DefaultAppRole;

    /// <summary>
    /// When true, app-only (client-credentials) tokens — those carrying a <c>roles</c>
    /// claim and NO <c>scp</c> claim — are accepted, provided the required app role is
    /// present. Delegated (user) tokens are unaffected. Defaults to <c>false</c>: unset
    /// configuration behaves exactly as before this flag existed and rejects app-only
    /// tokens.
    /// </summary>
    public bool AllowAppOnlyTokens { get; init; }

    /// <summary>
    /// Optional allow-list of application (client) IDs that may authenticate via
    /// app-only tokens when <see cref="AllowAppOnlyTokens"/> is <c>true</c>. An empty
    /// list means no client-ID restriction — every app-only token bearing the required
    /// app role is accepted. When populated, the token's <c>azp</c> (v2) or
    /// <c>appid</c> (v1) claim MUST match one of the listed GUIDs.
    /// </summary>
    public string[] AllowedAppClientIds { get; init; } = [];

    /// <summary>When true, real JWT bearer validation is enforced. Defaults to true outside Development.</summary>
    public bool RequireAuth { get; init; } = true;

    /// <summary>OIDC authority the JwtBearer handler pins to: {Instance}{TenantId}/v2.0.</summary>
    public string Authority =>
        $"{Instance.TrimEnd('/')}/{TenantId}/v2.0";

    /// <summary>Token audiences accepted by the API (client id and api:// form).</summary>
    public string[] ValidAudiences =>
        !string.IsNullOrWhiteSpace(Audience)
            ? [Audience]
            : ClientId is { Length: > 0 }
                ? [ClientId, $"api://{ClientId}"]
                : [];

    /// <summary>Tenant-pinned issuers accepted by the API (v2.0 and v1 sts forms).</summary>
    public string[] ValidIssuers =>
        TenantId is { Length: > 0 }
            ? [$"{Instance.TrimEnd('/')}/{TenantId}/v2.0", $"https://sts.windows.net/{TenantId}/"]
            : [];

    /// <summary>
    /// Resolves options from configuration and fails fast for any non-Development
    /// environment when tenant/audience/client identifiers are missing or when auth is
    /// disabled. Production (and every other non-Development environment) can NEVER run
    /// with <c>Security:RequireAuth=false</c>.
    /// </summary>
    public static EntraAuthOptions FromConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        IConfigurationSection entra = configuration.GetSection(SectionName);

        // Backward-compatible fallbacks to the legacy Security:* keys. Placeholder-looking
        // values (e.g. "<your-tenant-id>") are scrubbed so they can never masquerade as
        // real configuration or win precedence over a genuine MicrosoftEntra value.
        string? legacyAuthority = Clean(configuration["Security:JwtAuthority"]);
        string? legacyAudience = Clean(configuration["Security:JwtAudience"]);

        bool nonDevelopment = !environment.IsDevelopment();

        // The RequireAuth flag may only relax authentication in Development. Outside
        // Development it is pinned on: an explicit false is rejected at startup so a
        // misconfigured deploy fails closed instead of silently serving anonymous traffic.
        bool requireAuthFlag = configuration.GetValue("Security:RequireAuth", nonDevelopment);
        if (nonDevelopment && !requireAuthFlag)
        {
            throw new InvalidOperationException(
                $"Security:RequireAuth=false is not permitted in the '{environment.EnvironmentName}' " +
                "environment. Microsoft Entra authentication is mandatory outside Development.");
        }

        bool requireAuth = nonDevelopment || requireAuthFlag;

        // Precedence: a real MicrosoftEntra value always wins over the legacy Security:* key.
        // Legacy keys are consulted only when the MicrosoftEntra value is genuinely absent.
        string? tenantId = Clean(entra["TenantId"]) ?? DeriveTenantFromAuthority(legacyAuthority);
        string? clientId = Clean(entra["ClientId"]);
        string? audience = Clean(entra["Audience"]) ?? legacyAudience;

        // Opt-in: accept app-only (client-credentials) tokens. Default false — an unset
        // deployment behaves exactly as before this feature existed. See docs/security.md
        // §"App-only (client-credentials) tokens" and docs/authentication-matrix.md.
        bool allowAppOnly = configuration.GetValue($"{SectionName}:AllowAppOnlyTokens", false);
        string[] allowedAppClientIds = ReadAllowedAppClientIds(entra);

        var options = new EntraAuthOptions
        {
            Instance = Clean(entra["Instance"]) ?? "https://login.microsoftonline.com/",
            TenantId = tenantId,
            ClientId = clientId,
            Audience = audience,
            ApiScope = Clean(entra["ApiScope"]) ?? DefaultApiScope,
            AppRole = Clean(entra["AppRole"]) ?? DefaultAppRole,
            AllowAppOnlyTokens = allowAppOnly,
            AllowedAppClientIds = allowedAppClientIds,
            RequireAuth = requireAuth,
        };

        // Fail-closed validation for the opt-in runs in EVERY environment (Development
        // included). If a deployment opts in, it must do so correctly regardless of where
        // it runs — no silent fallback to a weaker policy on a misconfigured opt-in.
        if (allowAppOnly)
        {
            options.ValidateAppOnlyOptIn();
        }

        // Validation runs for ALL non-Development environments, regardless of the flag.
        if (nonDevelopment)
        {
            options.Validate();
        }

        return options;
    }

    /// <summary>
    /// Fail-fast validation: real auth cannot run without a tenant and an audience/client id,
    /// and documentation placeholders (e.g. "&lt;your-tenant-id&gt;") are rejected.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TenantId) || IsPlaceholder(TenantId))
        {
            throw new InvalidOperationException(
                "MicrosoftEntra:TenantId is required (and must not be a placeholder) outside Development. " +
                "Set it via configuration or the MicrosoftEntra__TenantId environment variable.");
        }

        if (ValidAudiences.Length == 0 || Array.Exists(ValidAudiences, IsPlaceholder))
        {
            throw new InvalidOperationException(
                "MicrosoftEntra:ClientId (or MicrosoftEntra:Audience / Security:JwtAudience) is required " +
                "(and must not be a placeholder) outside Development so the API can validate the token audience.");
        }
    }

    /// <summary>
    /// Fail-fast validation for the app-only opt-in. Runs whenever
    /// <see cref="AllowAppOnlyTokens"/> is <c>true</c> so a misconfigured opt-in never
    /// silently falls through to a weaker policy — it fails startup instead.
    /// </summary>
    public void ValidateAppOnlyOptIn()
    {
        if (string.IsNullOrWhiteSpace(AppRole) || IsPlaceholder(AppRole))
        {
            throw new InvalidOperationException(
                "MicrosoftEntra:AppRole is required (and must not be a placeholder) when " +
                "MicrosoftEntra:AllowAppOnlyTokens=true. App-only tokens are authorized " +
                "solely by the configured app role, so leaving it unset would grant access " +
                "to any client-credentials caller.");
        }

        foreach (string entry in AllowedAppClientIds)
        {
            if (string.IsNullOrWhiteSpace(entry) || IsPlaceholder(entry))
            {
                throw new InvalidOperationException(
                    "MicrosoftEntra:AllowedAppClientIds contains a blank or placeholder " +
                    "entry. Every allow-list entry must be a real application (client) ID " +
                    "so a typo cannot silently disable the restriction.");
            }

            if (!Guid.TryParse(entry, out _))
            {
                throw new InvalidOperationException(
                    $"MicrosoftEntra:AllowedAppClientIds contains '{entry}', which is not a " +
                    "valid GUID. Every entry must be an application (client) ID from the " +
                    "Entra tenant so app-only requests can be matched against the token's " +
                    "azp/appid claim.");
            }
        }
    }

    /// <summary>Detects documentation placeholders such as "&lt;your-tenant-id&gt;".</summary>
    private static bool IsPlaceholder(string? value) =>
        !string.IsNullOrWhiteSpace(value) && (value.Contains('<') || value.Contains('>'));

    /// <summary>Trims and normalizes a configuration value, treating placeholders as absent (null).</summary>
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return IsPlaceholder(trimmed) ? null : trimmed;
    }

    private static string? DeriveTenantFromAuthority(string? authority)
    {
        if (string.IsNullOrWhiteSpace(authority))
        {
            return null;
        }

        // https://login.microsoftonline.com/{tenant}/v2.0
        return Uri.TryCreate(authority, UriKind.Absolute, out Uri? uri) && uri.Segments.Length >= 2
            ? uri.Segments[1].TrimEnd('/')
            : null;
    }

    /// <summary>
    /// Reads the optional <c>MicrosoftEntra:AllowedAppClientIds</c> configuration array,
    /// preserving raw values so <see cref="ValidateAppOnlyOptIn"/> can reject placeholders
    /// and malformed GUIDs at startup. Blank entries are dropped so a trailing empty slot
    /// in configuration is treated as absent (not as a validation failure).
    /// </summary>
    private static string[] ReadAllowedAppClientIds(IConfigurationSection entra)
    {
        IConfigurationSection section = entra.GetSection("AllowedAppClientIds");
        if (!section.Exists())
        {
            return [];
        }

        List<string> entries = [];
        foreach (IConfigurationSection child in section.GetChildren())
        {
            string? raw = child.Value;
            if (raw is null)
            {
                continue;
            }

            string trimmed = raw.Trim();
            if (trimmed.Length > 0)
            {
                entries.Add(trimmed);
            }
        }

        return [.. entries];
    }
}

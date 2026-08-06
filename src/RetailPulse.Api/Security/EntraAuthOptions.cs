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

        var options = new EntraAuthOptions
        {
            Instance = Clean(entra["Instance"]) ?? "https://login.microsoftonline.com/",
            TenantId = tenantId,
            ClientId = clientId,
            Audience = audience,
            ApiScope = Clean(entra["ApiScope"]) ?? DefaultApiScope,
            AppRole = Clean(entra["AppRole"]) ?? DefaultAppRole,
            RequireAuth = requireAuth,
        };

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
}

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
    /// Resolves options from configuration and fails fast for a production/real-auth
    /// posture when tenant or client identifiers are missing.
    /// </summary>
    public static EntraAuthOptions FromConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        IConfigurationSection entra = configuration.GetSection(SectionName);

        // Backward-compatible fallbacks to the legacy Security:* keys.
        string? legacyAuthority = configuration["Security:JwtAuthority"];
        string? legacyAudience = configuration["Security:JwtAudience"];

        bool requireAuth = configuration.GetValue(
            "Security:RequireAuth",
            !environment.IsDevelopment());

        var options = new EntraAuthOptions
        {
            Instance = entra["Instance"] is { Length: > 0 } inst
                ? inst
                : "https://login.microsoftonline.com/",
            TenantId = entra["TenantId"] is { Length: > 0 } tid
                ? tid
                : DeriveTenantFromAuthority(legacyAuthority),
            ClientId = entra["ClientId"],
            Audience = entra["Audience"] is { Length: > 0 } aud ? aud : legacyAudience,
            ApiScope = entra["ApiScope"] is { Length: > 0 } scope ? scope : DefaultApiScope,
            AppRole = entra["AppRole"] is { Length: > 0 } role ? role : DefaultAppRole,
            RequireAuth = requireAuth,
        };

        if (requireAuth && !environment.IsDevelopment())
        {
            options.Validate();
        }

        return options;
    }

    /// <summary>
    /// Fail-fast validation: real auth cannot run without a tenant and an audience/client id.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TenantId))
        {
            throw new InvalidOperationException(
                "MicrosoftEntra:TenantId is required when Security:RequireAuth=true. " +
                "Set it via configuration or the MicrosoftEntra__TenantId environment variable.");
        }

        if (ValidAudiences.Length == 0)
        {
            throw new InvalidOperationException(
                "MicrosoftEntra:ClientId (or MicrosoftEntra:Audience / Security:JwtAudience) is required " +
                "when Security:RequireAuth=true so the API can validate the token audience.");
        }
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

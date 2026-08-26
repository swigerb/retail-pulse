using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using RetailPulse.Api.Auth;

namespace RetailPulse.Api.Security;

/// <summary>
/// Centralised, tenant-scoped Entra authentication + authorization wiring for the API.
///
/// This is the single security boundary for the deployed SWA + ACA architecture:
/// <list type="bullet">
///   <item>Production enforces real Microsoft Entra JWT bearer validation pinned to the
///     configured tenant authority, audience, and issuer.</item>
///   <item>The <c>access_token</c> query parameter is honoured <b>only</b> for SignalR
///     <c>/hubs</c> requests (browsers cannot set Authorization headers on WebSocket
///     handshakes); all other endpoints require the Authorization header.</item>
///   <item>Every protected endpoint/hub requires an authenticated user, the required app
///     role (<c>roles</c> claim), and either the delegated API scope (<c>scp</c> claim) or
///     — only when <see cref="EntraAuthOptions.AllowAppOnlyTokens"/> is opted in — an
///     app-only (client-credentials) token bearing the required app role and, if
///     configured, an allow-listed <c>azp</c>/<c>appid</c>.</item>
///   <item>Development uses the synthetic <see cref="DevelopmentAuthHandler"/> so the demo
///     runs locally without real tokens — never available outside Development.</item>
/// </list>
/// The default authorization policy is intentionally strong: it never degrades to
/// <c>RequireAssertion(_ =&gt; true)</c> when auth is required.
/// </summary>
public static class AuthenticationSetup
{
    /// <summary>Authorization policy enforced by every protected endpoint and hub.</summary>
    public const string UserPolicy = "RetailPulseUser";

    /// <summary>Path prefix whose requests may carry the bearer token as an access_token query param.</summary>
    private const string _hubPathPrefix = "/hubs";

    /// <summary>
    /// Registers authentication for the API and returns the resolved Entra options so the
    /// caller can wire the matching authorization policy.
    /// </summary>
    public static EntraAuthOptions AddRetailPulseAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = EntraAuthOptions.FromConfiguration(configuration, environment);
        services.AddSingleton(options);

        if (environment.IsDevelopment())
        {
            // Synthetic identity for local demo; a real JWT scheme remains available (but
            // not default) so developers can exercise token flows against the dev host.
            services.AddAuthentication(DevelopmentAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthHandler>(
                    DevelopmentAuthHandler.SchemeName, _ => { })
                .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme,
                    jwt => ConfigureJwtBearer(jwt, options));
        }
        else
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme,
                    jwt => ConfigureJwtBearer(jwt, options));
        }

        return options;
    }

    /// <summary>
    /// Registers the authorization policy set. When auth is required the default policy
    /// demands an authenticated user, the required app role, and the required API scope.
    /// When auth is disabled (local experiments only) the default policy still requires an
    /// authenticated user — it is never permissive.
    ///
    /// A deny-by-default <see cref="AuthorizationOptions.FallbackPolicy"/> is also set to the
    /// same strong policy so that ANY endpoint without explicit authorization metadata is
    /// protected. Only endpoints that opt out with <c>.AllowAnonymous()</c> (health/liveness)
    /// are reachable unauthenticated — a forgotten <c>.RequireAuthorization()</c> on a future
    /// <c>/api</c> or <c>/hubs</c> route can no longer expose a billable anonymous path.
    /// </summary>
    public static void AddRetailPulseAuthorization(this IServiceCollection services, EntraAuthOptions options)
    {
        services.AddAuthorization(authz =>
        {
            AuthorizationPolicy policy = BuildUserPolicy(options);
            authz.AddPolicy(UserPolicy, policy);
            authz.DefaultPolicy = policy;
            authz.FallbackPolicy = policy;
        });
    }

    /// <summary>
    /// Builds the user policy. Exposed for tests and for explicit <c>RequireAuthorization(UserPolicy)</c>.
    /// </summary>
    public static AuthorizationPolicy BuildUserPolicy(EntraAuthOptions options)
    {
        AuthorizationPolicyBuilder builder = new AuthorizationPolicyBuilder().RequireAuthenticatedUser();

        if (options.RequireAuth)
        {
            builder
                .RequireRole(options.AppRole)
                .RequireAssertion(ctx => IsAuthorizedPrincipal(ctx.User, options));
        }

        return builder.Build();
    }

    /// <summary>
    /// Configures JwtBearer for tenant-pinned validation and the hub-only query-token mapping.
    /// </summary>
    public static void ConfigureJwtBearer(JwtBearerOptions jwt, EntraAuthOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.TenantId))
        {
            jwt.Authority = options.Authority;
        }

        jwt.MapInboundClaims = false;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            // Issuer/audience validation is unconditional whenever auth is required (always
            // the case outside Development). It never silently disables itself because the
            // configured issuer/audience list happens to be empty — a misconfigured deploy
            // then rejects every token instead of accepting unvalidated ones.
            ValidateIssuer = options.RequireAuth || options.ValidIssuers.Length > 0,
            ValidIssuers = options.ValidIssuers,
            ValidateAudience = options.RequireAuth || options.ValidAudiences.Length > 0,
            ValidAudiences = options.ValidAudiences,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(5),
            // Entra emits app roles in "roles" and delegated scopes in "scp".
            RoleClaimType = "roles",
            NameClaimType = "name",
        };

        jwt.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // WebSocket/SSE handshakes cannot set an Authorization header, so SignalR
                // clients pass the token as ?access_token=. Honour this ONLY for /hubs to
                // avoid widening the token surface for ordinary REST endpoints.
                PathString path = context.HttpContext.Request.Path;
                if (path.StartsWithSegments(_hubPathPrefix))
                {
                    string? queryToken = context.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(queryToken))
                    {
                        context.Token = queryToken;
                    }
                }

                return Task.CompletedTask;
            },
        };
    }

    /// <summary>
    /// Returns true when the principal is authorized under the dual-mode token policy:
    /// <list type="bullet">
    ///   <item>Delegated tokens (carrying <c>scp</c>) MUST carry the configured API scope.
    ///     Behaviour is byte-for-byte unchanged from before the app-only opt-in existed —
    ///     and is unaffected by whether the opt-in is on or off.</item>
    ///   <item>App-only tokens (carrying <c>roles</c> but NO <c>scp</c>) are accepted ONLY
    ///     when <see cref="EntraAuthOptions.AllowAppOnlyTokens"/> is <c>true</c>. The
    ///     required app role is already enforced by <c>RequireRole</c> on the policy; if
    ///     <see cref="EntraAuthOptions.AllowedAppClientIds"/> is populated, the token's
    ///     <c>azp</c>/<c>appid</c> must additionally match one of the listed client IDs.</item>
    ///   <item>Tokens carrying neither <c>scp</c> nor <c>roles</c> are rejected.</item>
    /// </list>
    /// </summary>
    public static bool IsAuthorizedPrincipal(ClaimsPrincipal principal, EntraAuthOptions options)
    {
        if (HasScopeClaim(principal))
        {
            // Delegated (user) token — behaviour unchanged: require the configured scope.
            return HasRequiredScope(principal, options.ApiScope);
        }

        if (HasRolesClaim(principal))
        {
            // App-only (client-credentials) token. The required app role is already
            // enforced by RequireRole on the policy. This branch decides whether the
            // opt-in accepts app-only at all and, if configured, whether the calling
            // client is on the allow-list.
            return options.AllowAppOnlyTokens
                && (options.AllowedAppClientIds.Length == 0
                    || HasAllowedAppClientId(principal, options.AllowedAppClientIds));
        }

        // Neither scp nor roles → no authorization signal at all → reject.
        return false;
    }

    /// <summary>
    /// Returns true when the principal carries the required delegated scope. The <c>scp</c>
    /// claim is a space-delimited list, so a single claim can contain several scopes.
    /// </summary>
    public static bool HasRequiredScope(ClaimsPrincipal principal, string requiredScope)
    {
        if (string.IsNullOrWhiteSpace(requiredScope))
        {
            return true;
        }

        foreach (Claim claim in principal.FindAll("scp"))
        {
            if (ContainsScope(claim.Value, requiredScope))
            {
                return true;
            }
        }

        // Some tokens use the long "http://schemas.microsoft.com/identity/claims/scope" form.
        foreach (Claim claim in principal.FindAll("http://schemas.microsoft.com/identity/claims/scope"))
        {
            if (ContainsScope(claim.Value, requiredScope))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsScope(string scopeClaimValue, string requiredScope)
    {
        foreach (string scope in scopeClaimValue.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(scope, requiredScope, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns true when the principal has any <c>scp</c>-style claim (v1 short or v2 long form).</summary>
    private static bool HasScopeClaim(ClaimsPrincipal principal) =>
        principal.HasClaim(c =>
            c.Type is "scp" or "http://schemas.microsoft.com/identity/claims/scope");

    /// <summary>Returns true when the principal has any <c>roles</c> claim.</summary>
    private static bool HasRolesClaim(ClaimsPrincipal principal) =>
        principal.HasClaim(c => c.Type == "roles");

    /// <summary>
    /// Returns true when the token's <c>azp</c> (v2) or <c>appid</c> (v1) claim matches
    /// one of the allow-listed application (client) IDs. Comparison is GUID-normalized so
    /// case, formatting, and braces cannot bypass the check.
    /// </summary>
    private static bool HasAllowedAppClientId(ClaimsPrincipal principal, IReadOnlyList<string> allowedClientIds)
    {
        var allowed = new Guid[allowedClientIds.Count];
        for (int i = 0; i < allowedClientIds.Count; i++)
        {
            if (!Guid.TryParse(allowedClientIds[i], out allowed[i]))
            {
                // Startup validation already rejects non-GUID entries — an unparseable
                // entry here means someone bypassed FromConfiguration/Validate. Fail
                // closed rather than treating it as a match.
                return false;
            }
        }

        foreach (Claim claim in principal.FindAll("azp"))
        {
            if (MatchesAny(claim.Value, allowed))
            {
                return true;
            }
        }

        foreach (Claim claim in principal.FindAll("appid"))
        {
            if (MatchesAny(claim.Value, allowed))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesAny(string tokenClientId, Guid[] allowed)
    {
        if (!Guid.TryParse(tokenClientId, out Guid parsed))
        {
            return false;
        }

        foreach (Guid candidate in allowed)
        {
            if (candidate == parsed)
            {
                return true;
            }
        }

        return false;
    }
}

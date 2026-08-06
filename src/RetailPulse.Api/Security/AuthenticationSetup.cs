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
///     role (<c>roles</c> claim), and the delegated API scope (<c>scp</c> claim).</item>
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
                .RequireAssertion(ctx => HasRequiredScope(ctx.User, options.ApiScope));
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
}

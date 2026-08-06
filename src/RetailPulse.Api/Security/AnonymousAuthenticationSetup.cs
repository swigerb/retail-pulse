using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using RetailPulse.Api.Auth;
using RetailPulse.Api.Security.Anonymous;

namespace RetailPulse.Api.Security;

/// <summary>
/// Authentication + authorization wiring for the Anonymous mode.
///
/// Mirrors the Entra boundary's shape (a single JwtBearer scheme, a strong default AND fallback
/// authorization policy, hub-only <c>?access_token</c> mapping) but validates the app's OWN
/// short-lived session tokens with the local HMAC signing key instead of Entra. The policy is
/// deliberately narrow and non-degrading:
/// <list type="bullet">
///   <item>an authenticated user is always required;</item>
///   <item>the constrained anonymous role and scope are required;</item>
///   <item>the token's <c>provider</c> claim must equal <c>Anonymous</c>, so an Entra or any
///     cross-provider token can never satisfy the anonymous policy.</item>
/// </list>
/// Only <c>/health</c>, <c>/alive</c>, and the bootstrap endpoint opt out with AllowAnonymous.
/// </summary>
public static class AnonymousAuthenticationSetup
{
    public const string AnonymousPolicy = "RetailPulseAnonymous";
    private const string _hubPathPrefix = "/hubs";

    /// <summary>
    /// Registers the anonymous session services, the JwtBearer scheme that validates the app's
    /// own session tokens, and the constrained authorization policy (as both default and
    /// fallback). Called by the provider-neutral factory for the Anonymous mode.
    /// </summary>
    public static void AddAnonymousAuthentication(
        this IServiceCollection services,
        AnonymousAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        var keyProvider = new AnonymousSigningKeyProvider(options);
        services.AddSingleton(keyProvider);
        services.AddSingleton<IAnonymousSessionTokenService, AnonymousSessionTokenService>();
        services.AddSingleton<IPrincipalNormalizer, AnonymousPrincipalNormalizer>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, jwt => ConfigureJwtBearer(jwt, keyProvider, options));

        services.AddAuthorization(authz =>
        {
            AuthorizationPolicy policy = BuildAnonymousPolicy(options);
            authz.AddPolicy(AnonymousPolicy, policy);
            authz.DefaultPolicy = policy;
            authz.FallbackPolicy = policy;
        });
    }

    /// <summary>
    /// Builds the constrained anonymous authorization policy: authenticated + anonymous role +
    /// anonymous scope + provider==Anonymous. Exposed for tests.
    /// </summary>
    public static AuthorizationPolicy BuildAnonymousPolicy(AnonymousAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireRole(options.Role)
            .RequireAssertion(ctx => HasScope(ctx.User, options.Scope))
            .RequireAssertion(ctx => string.Equals(
                ctx.User.FindFirst(AnonymousCapabilityPolicy.ProviderClaimType)?.Value,
                AnonymousCapabilityPolicy.ProviderName,
                StringComparison.Ordinal))
            .Build();
    }

    private static void ConfigureJwtBearer(
        JwtBearerOptions jwt,
        AnonymousSigningKeyProvider keyProvider,
        AnonymousAuthOptions options)
    {
        jwt.MapInboundClaims = false;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = [options.Issuer],
            ValidateAudience = true,
            ValidAudiences = [options.Audience],
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keyProvider.ValidationKeys,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = "roles",
            NameClaimType = JwtRegisteredClaimNames.Sub,
        };

        jwt.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // WebSocket handshakes cannot set Authorization; SignalR passes ?access_token=.
                // Honour it ONLY for /hubs so REST endpoints keep header-only tokens (a query
                // token on a REST path is therefore ignored and the request is rejected).
                if (context.HttpContext.Request.Path.StartsWithSegments(_hubPathPrefix))
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

    private static bool HasScope(ClaimsPrincipal principal, string requiredScope)
    {
        if (string.IsNullOrWhiteSpace(requiredScope))
        {
            return true;
        }

        foreach (Claim claim in principal.FindAll("scp"))
        {
            foreach (string scope in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(scope, requiredScope, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

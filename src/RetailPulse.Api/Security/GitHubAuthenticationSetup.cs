using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using RetailPulse.Api.Auth;
using RetailPulse.Api.Security.GitHub;

namespace RetailPulse.Api.Security;

/// <summary>
/// Authentication + authorization wiring for the GitHub mode.
///
/// Mirrors the Entra/Anonymous boundary shape (a single JwtBearer scheme validating the app's OWN
/// short-lived session token with a local HMAC key, a strong default AND fallback authorization
/// policy, hub-only <c>?access_token</c> mapping) but binds the policy to the GitHub provider:
/// <list type="bullet">
///   <item>an authenticated user is always required;</item>
///   <item>the <c>RetailPulse.User</c> role and <c>access_as_user</c> scope are required (full
///     authenticated capabilities are acceptable ONLY because they are reached after server-side
///     allowlist verification at login);</item>
///   <item>the token's <c>provider</c> claim must equal <c>GitHub</c>, so an Entra, Anonymous, or any
///     other cross-provider token can never satisfy the GitHub policy (and vice versa).</item>
/// </list>
/// Only <c>/health</c>, <c>/alive</c>, and the three narrowly-anonymous BFF endpoints
/// (<c>start</c> / <c>callback</c> / <c>exchange</c>) opt out with AllowAnonymous.
/// </summary>
public static class GitHubAuthenticationSetup
{
    public const string GitHubPolicy = "RetailPulseGitHub";
    private const string _hubPathPrefix = "/hubs";

    /// <summary>
    /// Registers the GitHub session services (options, signing key, token service, one-time stores,
    /// OAuth client, allowlist, normalizer), the JwtBearer scheme that validates the app's own
    /// session tokens, and the constrained authorization policy (as both default and fallback).
    /// </summary>
    public static void AddGitHubAuthentication(
        this IServiceCollection services,
        GitHubAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        var keyProvider = new GitHubSigningKeyProvider(options);
        services.AddSingleton(keyProvider);
        services.AddSingleton<IGitHubSessionTokenService, GitHubSessionTokenService>();
        services.AddSingleton<GitHubStateStore>();
        services.AddSingleton<GitHubRedemptionStore>();
        services.AddSingleton<GitHubUserAllowlist>();
        services.AddSingleton<IPrincipalNormalizer, GitHubPrincipalNormalizer>();

        // Confidential OAuth transport. Auto-redirect is DISABLED so a crafted 3xx from GitHub cannot
        // bounce the request — or a bearer token — to another host (SSRF/redirect defense). The client
        // talks only to the fixed endpoints in GitHubAuthConstants.
        services.AddHttpClient<IGitHubOAuthClient, GitHubOAuthClient>(http =>
            {
                http.Timeout = TimeSpan.FromSeconds(10);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("RetailPulse-Auth/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
            });

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, jwt => ConfigureJwtBearer(jwt, keyProvider, options));

        services.AddAuthorization(authz =>
        {
            AuthorizationPolicy policy = BuildGitHubPolicy(options);
            authz.AddPolicy(GitHubPolicy, policy);
            authz.DefaultPolicy = policy;
            authz.FallbackPolicy = policy;
        });
    }

    /// <summary>
    /// Builds the GitHub authorization policy: authenticated + role + scope + provider==GitHub.
    /// Exposed for tests.
    /// </summary>
    public static AuthorizationPolicy BuildGitHubPolicy(GitHubAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireRole(options.Role)
            .RequireAssertion(ctx => HasScope(ctx.User, options.Scope))
            .RequireAssertion(ctx => string.Equals(
                ctx.User.FindFirst(GitHubAuthConstants.ProviderClaimType)?.Value,
                GitHubAuthConstants.ProviderName,
                StringComparison.Ordinal))
            .Build();
    }

    private static void ConfigureJwtBearer(
        JwtBearerOptions jwt,
        GitHubSigningKeyProvider keyProvider,
        GitHubAuthOptions options)
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
                // Honour it ONLY for /hubs so REST endpoints keep header-only tokens (a query token on
                // a REST path is therefore ignored and the request is rejected — identical to Entra).
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

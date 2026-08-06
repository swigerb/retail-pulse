using RetailPulse.Api.Security;
using RetailPulse.Api.Security.Anonymous;

namespace RetailPulse.Api.Endpoints;

/// <summary>
/// The single, narrowly-scoped unauthenticated surface of Anonymous mode: a bootstrap endpoint
/// that mints a short-lived anonymous session credential for a future frontend (Sprint 3).
///
/// It creates a cryptographically random per-session subject SERVER-SIDE (never from a client
/// header or body) and returns a signed, short-TTL Retail Pulse session token usable both as a
/// REST <c>Authorization: Bearer</c> and as a SignalR <c>?access_token</c>. It is rate-limited
/// per-IP, exposes no other anonymous API, carries no PII, and issues no refresh token — the
/// client re-bootstraps when the token expires, which bounds the replay window to the TTL.
/// Mapped only when <c>Authentication:Mode=Anonymous</c>.
/// </summary>
public static class AnonymousAuthEndpoints
{
    public const string BootstrapRateLimitPolicy = "anonymous-bootstrap";

    public static WebApplication MapAnonymousAuthEndpoints(this WebApplication app)
    {
        app.MapPost(AnonymousCapabilityPolicy.BootstrapRoute,
            (IAnonymousSessionTokenService tokenService) =>
            {
                AnonymousSession session = tokenService.CreateSession();
                return Results.Ok(new
                {
                    token = session.Token,
                    tokenType = "Bearer",
                    expiresInSeconds = session.ExpiresInSeconds,
                    subject = session.Subject,
                });
            })
            .AllowAnonymous()
            .RequireRateLimiting(BootstrapRateLimitPolicy)
            .WithName("AnonymousSessionBootstrap")
            .WithTags("Authentication");

        return app;
    }
}

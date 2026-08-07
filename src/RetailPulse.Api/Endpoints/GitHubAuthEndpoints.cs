using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using RetailPulse.Api.Security;
using RetailPulse.Api.Security.GitHub;

namespace RetailPulse.Api.Endpoints;

/// <summary>
/// The three narrowly-anonymous endpoints of the GitHub confidential Backend-for-Frontend (BFF)
/// OAuth flow. Mapped only when <c>Authentication:Mode=GitHub</c>. The GitHub PROVIDER token never
/// reaches the browser — it is used transiently on the server to validate the user and read org
/// membership, then discarded. The SPA only ever receives a short-lived one-time redemption code and,
/// after exchanging it, a short-lived Retail Pulse session token.
///
/// Flow:
/// <list type="number">
///   <item><b>start</b> (GET) — mints a random state bound to an HttpOnly/Secure/SameSite=Lax cookie
///     and a server-side one-time store, then redirects to the fixed GitHub authorize URL with
///     minimal scopes. No user-supplied redirect target is honoured (open-redirect closed).</item>
///   <item><b>callback</b> (GET) — validates state + cookie + TTL + one-use BEFORE any code exchange,
///     exchanges the code server-side, validates the token via <c>/user</c>, verifies the server-side
///     allowlist, and redirects to the ONE configured SPA URL carrying only a one-time redemption
///     code (never a provider/app token). Denials and errors are handled without leaking anything.</item>
///   <item><b>exchange</b> (POST) — requires the exact per-code browser-bound <c>__Host-</c> redemption
///     cookie set at callback (constant-time hash compare), atomically redeems the one-time code, and
///     returns a short-lived Retail Pulse GitHub session token (no refresh token). A stolen unused code
///     replayed from another browser fails (no cookie); replay/races are impossible (one-use); the exact
///     cookie is deleted on success and failure.</item>
/// </list>
/// </summary>
public static class GitHubAuthEndpoints
{
    public const string StartRateLimitPolicy = "github-start";
    public const string ExchangeRateLimitPolicy = "github-exchange";

    public static WebApplication MapGitHubAuthEndpoints(this WebApplication app)
    {
        app.MapGet(GitHubAuthConstants.StartRoute, StartAsync)
            .AllowAnonymous()
            .RequireRateLimiting(StartRateLimitPolicy)
            .WithName("GitHubAuthStart")
            .WithTags("Authentication");

        app.MapGet(GitHubAuthConstants.CallbackRoute, CallbackAsync)
            .AllowAnonymous()
            // The callback INTENTIONALLY shares the "github-start" fixed window: start + callback are the
            // two halves of one login attempt, so they are budgeted together against the same per-replica
            // window. Exchange has its own, separate window.
            .RequireRateLimiting(StartRateLimitPolicy)
            .WithName("GitHubAuthCallback")
            .WithTags("Authentication");

        app.MapPost(GitHubAuthConstants.ExchangeRoute, ExchangeAsync)
            .AllowAnonymous()
            .RequireRateLimiting(ExchangeRateLimitPolicy)
            .WithName("GitHubAuthExchange")
            .WithTags("Authentication");

        return app;
    }

    // ── start ──────────────────────────────────────────────────────────────────
    private static IResult StartAsync(
        HttpContext context,
        GitHubAuthOptions options,
        GitHubStateStore stateStore,
        TimeProvider? timeProvider = null)
    {
        TimeProvider clock = timeProvider ?? TimeProvider.System;

        // Random, unguessable state (server-side one-time entry) + a separate random secret placed in
        // the browser-bound cookie. Verification at callback requires BOTH — defeating login-CSRF and
        // state fixation (an attacker cannot both know our state and set our HttpOnly cookie).
        string state = GitHubRandom.NewToken();
        string cookieSecret = GitHubRandom.NewToken();
        long expiresAt = clock.GetUtcNow().UtcDateTime.AddSeconds(options.StateTtlSeconds).Ticks;

        if (!stateStore.TryStore(state, new GitHubStateEntry(Sha256(cookieSecret), expiresAt)))
        {
            return Results.Problem(
                title: "github_start_unavailable",
                detail: "The login state store is temporarily at capacity. Please retry shortly.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        AppendStateCookie(context, options, state, cookieSecret);

        // Redirect ONLY to the fixed GitHub authorize URL with minimal scopes. redirect_uri is the
        // exact registered callback; allow_signup=false avoids account-creation flows in a demo.
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = options.ClientId,
            ["redirect_uri"] = options.CallbackUrl,
            ["state"] = state,
            ["scope"] = options.RequestedScopes,
            ["allow_signup"] = "false",
        };
        string authorizeUrl = QueryHelpers.AddQueryString(GitHubAuthConstants.AuthorizeUrl, query);
        return Results.Redirect(authorizeUrl);
    }

    // ── callback ─────────────────────────────────────────────────────────────────
    private static async Task<IResult> CallbackAsync(
        HttpContext context,
        GitHubAuthOptions options,
        GitHubStateStore stateStore,
        GitHubRedemptionStore redemptionStore,
        IGitHubOAuthClient oauthClient,
        GitHubUserAllowlist allowlist,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
    {
        ILogger logger = loggerFactory.CreateLogger("GitHubAuthCallback");
        TimeProvider clock = timeProvider ?? TimeProvider.System;
        CancellationToken ct = context.RequestAborted;

        string? state = context.Request.Query["state"];

        // Validate the state FORMAT before anything else: it must be exactly the fixed-length base64url
        // shape our start emits. This bounds the cookie-name derivation below and rejects a malformed or
        // oversized callback up front. (Format-valid ≠ known; the one-time store still gates authenticity.)
        if (string.IsNullOrEmpty(state) || !GitHubStateCookie.IsValidStateFormat(state))
        {
            return Results.BadRequest(Problem("invalid_state", "Missing or invalid login state."));
        }

        // The cookie name is DERIVED from this specific state, so parallel login tabs never collide and
        // this callback reads/deletes ONLY its own cookie.
        string cookieName = GitHubStateCookie.NameFor(state, options.RequireSecureCookies);
        string? cookieSecret = context.Request.Cookies[cookieName];

        // The state cookie is one-use: always delete it, whatever the outcome.
        // 1) State + cookie must both be present, or this is not a genuine callback from our start.
        if (string.IsNullOrEmpty(cookieSecret))
        {
            DeleteStateCookie(context, options, state);
            return Results.BadRequest(Problem("invalid_state", "Missing or invalid login state."));
        }

        // 2) Consume the state entry atomically (one-time). Missing/expired ⇒ CSRF / replay / timeout.
        if (!stateStore.TryConsume(state, out GitHubStateEntry entry))
        {
            DeleteStateCookie(context, options, state);
            return Results.BadRequest(Problem("invalid_state", "The login state is unknown, already used, or expired."));
        }

        // 3) Bind the state to THIS browser: the cookie secret must hash to the stored value
        //    (constant-time). This defeats state fixation and a stolen/guessed state without the cookie.
        if (!CryptographicOperations.FixedTimeEquals(Sha256(cookieSecret), entry.CookieSecretHash))
        {
            DeleteStateCookie(context, options, state);
            return Results.BadRequest(Problem("invalid_state", "The login state does not match this browser."));
        }

        // The flow is now proven to originate from our start in this browser. Retire the cookie.
        DeleteStateCookie(context, options, state);

        // 4) The user may have denied consent — handle safely, no token, redirect to the fixed frontend.
        string? oauthError = context.Request.Query["error"];
        if (!string.IsNullOrEmpty(oauthError))
        {
            logger.LogInformation("GitHub login was not completed (provider error).");
            return RedirectToFrontend(options, "error", "access_denied");
        }

        string? code = context.Request.Query["code"];
        if (string.IsNullOrEmpty(code))
        {
            return Results.BadRequest(Problem("missing_code", "No authorization code was returned."));
        }

        // 5) Confidential server-side exchange. The provider token stays on the server.
        GitHubTokenResult tokenResult = await oauthClient.ExchangeCodeAsync(code, ct);
        if (!tokenResult.Success || string.IsNullOrEmpty(tokenResult.AccessToken))
        {
            logger.LogWarning("GitHub code exchange failed: {Reason}", tokenResult.Error);
            return RedirectToFrontend(options, "error", "login_failed");
        }

        string providerToken = tokenResult.AccessToken;

        // 6) Validate the provider token by reading the authenticated user (immutable id + login).
        GitHubUserResult userResult = await oauthClient.GetUserAsync(providerToken, ct);
        if (!userResult.Success)
        {
            logger.LogWarning("GitHub /user validation failed: {Reason}", userResult.Error);
            return RedirectToFrontend(options, "error", "login_failed");
        }

        var verified = new GitHubVerifiedUser(userResult.UserId, userResult.Login);

        // 7) Server-side allowlist (immutable numeric id / login / active org membership). Fail closed.
        GitHubAllowlistDecision decision = await allowlist.EvaluateAsync(verified, providerToken, ct);
        if (!decision.Allowed)
        {
            logger.LogInformation("GitHub user {UserId} denied by allowlist ({Reason}).", verified.UserId, decision.Reason);
            return RedirectToFrontend(options, "error", "not_authorized");
        }

        // 8) Mint a one-time redemption code bound to the verified identity (NOT a token) AND to THIS
        //    browser. A separate random secret is placed in a per-code __Host- redemption cookie; only
        //    its hash is stored server-side. The exchange must present both the code and the matching
        //    cookie (constant-time compared), so a stolen unused code replayed from another browser —
        //    which never received the cookie — can never be redeemed. The app session JWT is minted
        //    fresh at exchange so its short TTL starts when the SPA receives it.
        string redemptionCode = GitHubRandom.NewToken();
        string redemptionCookieSecret = GitHubRandom.NewToken();
        long redeemBy = clock.GetUtcNow().UtcDateTime.AddSeconds(options.RedemptionTtlSeconds).Ticks;
        if (!redemptionStore.TryStore(redemptionCode, new GitHubRedemptionEntry(verified, Sha256(redemptionCookieSecret), redeemBy)))
        {
            return Results.Problem(
                title: "github_callback_unavailable",
                detail: "The login redemption store is temporarily at capacity. Please retry shortly.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Bind the redemption to this browser: set the per-code __Host- cookie carrying the secret.
        AppendRedemptionCookie(context, options, redemptionCode, redemptionCookieSecret);

        // 9) Redirect to the ONE configured SPA URL carrying only the one-time redemption code.
        return RedirectToFrontend(options, "code", redemptionCode);
    }

    // ── exchange ─────────────────────────────────────────────────────────────────
    private static IResult ExchangeAsync(
        HttpContext context,
        [FromBody] GitHubExchangeRequest request,
        GitHubAuthOptions options,
        GitHubRedemptionStore redemptionStore,
        IGitHubSessionTokenService tokenService)
    {
        // The code must be present AND exactly the fixed base64url shape our callback emits — this bounds
        // the per-code cookie-name derivation and rejects any malformed/oversized code up front.
        if (request is null || string.IsNullOrWhiteSpace(request.Code) || !GitHubStateCookie.IsValidStateFormat(request.Code))
        {
            return Results.BadRequest(Problem("invalid_request", "A redemption code is required."));
        }

        string code = request.Code;

        // The redemption is bound to the browser that completed the callback: read the per-code cookie
        // and ALWAYS delete it, whatever the outcome (success or failure).
        string cookieName = GitHubStateCookie.RedemptionNameFor(code, options.RequireSecureCookies);
        string? cookieSecret = context.Request.Cookies[cookieName];
        DeleteRedemptionCookie(context, options, code);

        // 1) The per-code cookie must be present. A stolen unused code replayed from another browser has
        //    no such cookie, so it fails HERE — and, crucially, without consuming the victim's code.
        if (string.IsNullOrEmpty(cookieSecret))
        {
            return Results.BadRequest(Problem("invalid_code", "The redemption code is unknown, already used, or expired."));
        }

        // 2) Atomic one-time redemption — a second attempt (replay) or a race can never both succeed.
        if (!redemptionStore.TryConsume(code, out GitHubRedemptionEntry entry))
        {
            return Results.BadRequest(Problem("invalid_code", "The redemption code is unknown, already used, or expired."));
        }

        // 3) Bind to THIS browser: the presented cookie secret must hash to the stored value
        //    (constant-time). A wrong cookie fails even though the code existed (it is now consumed).
        if (!CryptographicOperations.FixedTimeEquals(Sha256(cookieSecret), entry.CookieSecretHash))
        {
            return Results.BadRequest(Problem("invalid_code", "The redemption code does not match this browser."));
        }

        GitHubSession session = tokenService.CreateSession(entry.User);
        return Results.Ok(new
        {
            token = session.Token,
            tokenType = "Bearer",
            expiresInSeconds = session.ExpiresInSeconds,
            subject = session.Subject,
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────────
    private static IResult RedirectToFrontend(GitHubAuthOptions options, string key, string value)
    {
        // The frontend URL is a fixed, validated, absolute HTTPS config value — never user input — so
        // this can never become an open redirect. Only a code OR an error code is ever appended.
        string url = QueryHelpers.AddQueryString(options.FrontendReturnUrl, key, value);
        return Results.Redirect(url);
    }

    private static string CookieName(GitHubAuthOptions options, string state) =>
        // Per-state name so parallel login tabs never clash. Secure/__Host semantics come from validated
        // configuration (RequireSecureCookies), NEVER from Request.IsHttps — behind a TLS-terminating
        // proxy (ACA) the container request is plain HTTP even though the browser↔edge hop is HTTPS.
        GitHubStateCookie.NameFor(state, options.RequireSecureCookies);

    private static void AppendStateCookie(HttpContext context, GitHubAuthOptions options, string state, string secret)
    {
        context.Response.Cookies.Append(CookieName(options, state), secret, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.RequireSecureCookies, // fixed by config, not the observed scheme
            SameSite = SameSiteMode.Lax, // top-level GET navigation back from github.com carries it
            Path = "/", // __Host- requires Path=/ and no Domain
            IsEssential = true,
            MaxAge = TimeSpan.FromSeconds(options.StateTtlSeconds),
        });
    }

    private static void DeleteStateCookie(HttpContext context, GitHubAuthOptions options, string state)
    {
        context.Response.Cookies.Delete(CookieName(options, state), new CookieOptions
        {
            HttpOnly = true,
            Secure = options.RequireSecureCookies,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });
    }

    private static string RedemptionCookieName(GitHubAuthOptions options, string code) =>
        // Per-code name so parallel logins never clash. Secure/__Host semantics come from validated
        // configuration (RequireSecureCookies), NEVER from Request.IsHttps — identical to the state cookie.
        GitHubStateCookie.RedemptionNameFor(code, options.RequireSecureCookies);

    private static void AppendRedemptionCookie(HttpContext context, GitHubAuthOptions options, string code, string secret)
    {
        context.Response.Cookies.Append(RedemptionCookieName(options, code), secret, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.RequireSecureCookies, // fixed by config, not the observed scheme
            SameSite = SameSiteMode.Lax, // the SPA's same-site exchange POST carries it
            Path = "/", // __Host- requires Path=/ and no Domain
            IsEssential = true,
            MaxAge = TimeSpan.FromSeconds(options.RedemptionTtlSeconds),
        });
    }

    private static void DeleteRedemptionCookie(HttpContext context, GitHubAuthOptions options, string code)
    {
        context.Response.Cookies.Delete(RedemptionCookieName(options, code), new CookieOptions
        {
            HttpOnly = true,
            Secure = options.RequireSecureCookies,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });
    }

    private static byte[] Sha256(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static object Problem(string code, string detail) => new
    {
        type = $"https://retail-pulse/errors/{code}",
        title = code,
        detail,
    };

    /// <summary>Body of the exchange request: the one-time redemption code from the callback redirect.</summary>
    public sealed record GitHubExchangeRequest(string? Code);
}

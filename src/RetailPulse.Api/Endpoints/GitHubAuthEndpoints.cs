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
///   <item><b>exchange</b> (POST) — atomically redeems the one-time code and returns a short-lived
///     Retail Pulse GitHub session token (no refresh token). Replay/races are impossible (one-use).</item>
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

        AppendStateCookie(context, options, cookieSecret);

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
        string? cookieSecret = context.Request.Cookies[CookieName(context)];

        // The state cookie is one-use: always delete it, whatever the outcome.
        // 1) State + cookie must both be present, or this is not a genuine callback from our start.
        if (string.IsNullOrEmpty(state) || string.IsNullOrEmpty(cookieSecret))
        {
            DeleteStateCookie(context, options);
            return Results.BadRequest(Problem("invalid_state", "Missing or invalid login state."));
        }

        // 2) Consume the state entry atomically (one-time). Missing/expired ⇒ CSRF / replay / timeout.
        if (!stateStore.TryConsume(state, out GitHubStateEntry entry))
        {
            DeleteStateCookie(context, options);
            return Results.BadRequest(Problem("invalid_state", "The login state is unknown, already used, or expired."));
        }

        // 3) Bind the state to THIS browser: the cookie secret must hash to the stored value
        //    (constant-time). This defeats state fixation and a stolen/guessed state without the cookie.
        if (!CryptographicOperations.FixedTimeEquals(Sha256(cookieSecret), entry.CookieSecretHash))
        {
            DeleteStateCookie(context, options);
            return Results.BadRequest(Problem("invalid_state", "The login state does not match this browser."));
        }

        // The flow is now proven to originate from our start in this browser. Retire the cookie.
        DeleteStateCookie(context, options);

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

        // 8) Mint a one-time redemption code bound to the verified identity (NOT a token). The app
        //    session JWT is minted fresh at exchange so its short TTL starts when the SPA receives it.
        string redemptionCode = GitHubRandom.NewToken();
        long redeemBy = clock.GetUtcNow().UtcDateTime.AddSeconds(options.RedemptionTtlSeconds).Ticks;
        if (!redemptionStore.TryStore(redemptionCode, new GitHubRedemptionEntry(verified, redeemBy)))
        {
            return Results.Problem(
                title: "github_callback_unavailable",
                detail: "The login redemption store is temporarily at capacity. Please retry shortly.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // 9) Redirect to the ONE configured SPA URL carrying only the one-time redemption code.
        return RedirectToFrontend(options, "code", redemptionCode);
    }

    // ── exchange ─────────────────────────────────────────────────────────────────
    private static IResult ExchangeAsync(
        [FromBody] GitHubExchangeRequest request,
        GitHubRedemptionStore redemptionStore,
        IGitHubSessionTokenService tokenService)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Code))
        {
            return Results.BadRequest(Problem("invalid_request", "A redemption code is required."));
        }

        // Atomic one-time redemption — a second attempt (replay) or a race can never both succeed.
        if (!redemptionStore.TryConsume(request.Code, out GitHubRedemptionEntry entry))
        {
            return Results.BadRequest(Problem("invalid_code", "The redemption code is unknown, already used, or expired."));
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

    private static string CookieName(HttpContext context) =>
        // The __Host- prefix (Secure + Path=/ + no Domain) is the strongest anti-fixation binding, but
        // it requires HTTPS. Fall back to a plain name only over plain HTTP (local dev / test host).
        context.Request.IsHttps ? GitHubAuthConstants.StateCookieName : "rp_gh_state";

    private static void AppendStateCookie(HttpContext context, GitHubAuthOptions options, string secret)
    {
        bool secure = context.Request.IsHttps;
        context.Response.Cookies.Append(CookieName(context), secret, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Lax, // top-level GET navigation back from github.com carries it
            Path = "/",
            IsEssential = true,
            MaxAge = TimeSpan.FromSeconds(options.StateTtlSeconds),
        });
    }

    private static void DeleteStateCookie(HttpContext context, GitHubAuthOptions options)
    {
        _ = options;
        bool secure = context.Request.IsHttps;
        context.Response.Cookies.Delete(CookieName(context), new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
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

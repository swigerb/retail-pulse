using System.Net;
using RetailPulse.Api.Security.Anonymous;

namespace RetailPulse.Api.Middleware;

/// <summary>
/// The single, central enforcement point for Anonymous-mode request restrictions. Registered only
/// when <c>Authentication:Mode=Anonymous</c>, after authentication/authorization so the validated
/// anonymous principal is available. It enforces — server-side, from the token, never from UI or a
/// client header — that an anonymous session may only:
/// <list type="bullet">
///   <item>reach read-only routes (GET/HEAD/OPTIONS or the explicit read-only-POST allowlist);
///     every other verb/route is a mutation and gets <c>403</c>;</item>
///   <item>send bodies within the configured size bound (<c>413</c> otherwise);</item>
///   <item>call model routes within per-subject and per-IP minute limits (<c>429</c>) and within
///     the global daily request/token/cost circuit breaker (<c>503</c> when tripped).</item>
/// </list>
/// A request slot is charged at admission — before the cache is consulted downstream — so cache
/// hits cannot bypass the request ceiling. A per-request timeout is also applied.
/// </summary>
public sealed class AnonymousGuardMiddleware : IMiddleware
{
    private readonly AnonymousAuthOptions _options;
    private readonly AnonymousRateLimiter _rateLimiter;
    private readonly AnonymousUsageBudget _budget;
    private readonly ILogger<AnonymousGuardMiddleware> _logger;

    public AnonymousGuardMiddleware(
        AnonymousAuthOptions options,
        AnonymousRateLimiter rateLimiter,
        AnonymousUsageBudget budget,
        ILogger<AnonymousGuardMiddleware> logger)
    {
        _options = options;
        _rateLimiter = rateLimiter;
        _budget = budget;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Only authenticated anonymous sessions are constrained here. Unauthenticated requests are
        // handled by the authorization policy (AllowAnonymous only on health + bootstrap).
        if (!AnonymousCapabilityPolicy.IsAnonymousPrincipal(context.User))
        {
            await next(context);
            return;
        }

        string method = context.Request.Method;
        string path = context.Request.Path.Value ?? string.Empty;

        // 1) Read-only enforcement — deny any mutation not on the explicit allowlist.
        if (AnonymousCapabilityPolicy.IsBlockedMutation(method, path))
        {
            await WriteProblem(context, HttpStatusCode.Forbidden,
                "anonymous_read_only",
                "This operation is not available in Anonymous mode. Anonymous sessions are read-only.");
            return;
        }

        // 2) Request-size bound.
        if (context.Request.ContentLength is long len && len > _options.MaxRequestBytes)
        {
            await WriteProblem(context, HttpStatusCode.RequestEntityTooLarge,
                "anonymous_request_too_large",
                $"Request body exceeds the anonymous limit of {_options.MaxRequestBytes} bytes.");
            return;
        }

        bool isModelRoute = IsBudgetedModelRoute(method, path);
        if (isModelRoute)
        {
            string subject = context.User.FindFirst(Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames.Sub)?.Value
                ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? "unknown";
            string ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            // 3) Per-subject and per-IP minute limits.
            if (!_rateLimiter.TryAcquire($"chat:sub:{subject}", _options.ChatPerSubjectPerMinute) ||
                !_rateLimiter.TryAcquire($"chat:ip:{ip}", _options.ChatPerIpPerMinute))
            {
                await WriteProblem(context, HttpStatusCode.TooManyRequests,
                    "anonymous_rate_limited",
                    "Anonymous request rate limit exceeded. Please retry shortly.");
                return;
            }

            // 4) Global daily circuit breaker — charge a request slot up-front so cache hits
            //    downstream still consume the request ceiling and cannot bypass it.
            if (!_budget.TryBeginRequest(out string? denyReason))
            {
                _logger.LogWarning("Anonymous daily budget denied a request: {Reason}", denyReason);
                await WriteProblem(context, HttpStatusCode.ServiceUnavailable,
                    "anonymous_budget_exhausted",
                    denyReason ?? "The anonymous daily budget has been exhausted. Please try again later.");
                return;
            }
        }

        // 5) Per-request timeout for anonymous callers.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
        CancellationToken original = context.RequestAborted;
        context.RequestAborted = timeoutCts.Token;
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !original.IsCancellationRequested)
        {
            if (!context.Response.HasStarted)
            {
                await WriteProblem(context, HttpStatusCode.GatewayTimeout,
                    "anonymous_request_timeout",
                    $"The request exceeded the anonymous timeout of {_options.RequestTimeoutSeconds} seconds.");
            }
        }
        finally
        {
            context.RequestAborted = original;
        }
    }

    private static bool IsBudgetedModelRoute(string method, string path)
    {
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        {
            return false;
        }

        string normalized = path.Length > 1 ? path.TrimEnd('/') : path;

        // Every anonymous-allowed non-GET route EXCEPT the bootstrap reaches real work/models and
        // is charged against the budget + chat limits. Bootstrap has its own per-IP limiter.
        return AnonymousCapabilityPolicy.AnonymousReadableNonGetRoutes.Contains(normalized)
            && !string.Equals(normalized, AnonymousCapabilityPolicy.BootstrapRoute, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteProblem(HttpContext context, HttpStatusCode status, string code, string detail)
    {
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new
        {
            type = $"https://retail-pulse/errors/{code}",
            title = code,
            status = (int)status,
            detail,
        });
    }
}

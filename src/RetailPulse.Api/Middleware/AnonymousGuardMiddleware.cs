using System.Buffers;
using System.Net;
using Microsoft.AspNetCore.Http.Features;
using RetailPulse.Api.Security.Anonymous;

namespace RetailPulse.Api.Middleware;

/// <summary>
/// The single, central enforcement point for Anonymous-mode request restrictions. Registered only
/// when <c>Authentication:Mode=Anonymous</c>, after authentication/authorization so the validated
/// anonymous principal is available. It enforces — server-side, from the token, never from UI or a
/// client header — that an anonymous session may only:
/// <list type="bullet">
///   <item>reach the explicit <see cref="AnonymousCapabilityPolicy"/> allowlist (deny-by-default):
///     every (method, route) that is not the bootstrap, <c>POST /api/chat</c>, or an allowed hub
///     gets <c>403</c>. There is NO read/GET shortcut;</item>
///   <item>send bodies within the configured size bound (<c>413</c> otherwise) — enforced before the
///     body is read and via a length-counting pre-read, so a chunked/unknown-length body that omits
///     <c>Content-Length</c> cannot bypass the cap;</item>
///   <item>call the model route within per-subject and per-IP minute limits (<c>429</c>) and within
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

        // 1) Deny-by-default authorization — reject any (method, route) not on the explicit
        //    anonymous allowlist (bootstrap, POST /api/chat, or an allowed hub). There is no
        //    read/GET shortcut: observability/admin/export/memory/cards/approvals/etc. are 403.
        if (AnonymousCapabilityPolicy.IsBlocked(method, path))
        {
            await WriteProblem(context, HttpStatusCode.Forbidden,
                "anonymous_forbidden",
                "This operation is not available in Anonymous mode.");
            return;
        }

        // 2) Request-size bound. Set the Kestrel per-request max BEFORE the body is read so a
        //    chunked / unknown-length body (no Content-Length) is capped during the read, then also
        //    reject early when a declared Content-Length already exceeds the limit.
        IHttpMaxRequestBodySizeFeature? sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = _options.MaxRequestBytes;
        }

        if (context.Request.ContentLength is long len && len > _options.MaxRequestBytes)
        {
            await WriteProblem(context, HttpStatusCode.RequestEntityTooLarge,
                "anonymous_request_too_large",
                $"Request body exceeds the anonymous limit of {_options.MaxRequestBytes} bytes.");
            return;
        }

        bool isModelRoute = AnonymousCapabilityPolicy.IsBudgetedModelRoute(method, path);
        if (isModelRoute)
        {
            // Length-counting pre-read: catches a chunked/unknown-length body that omits
            // Content-Length (the ContentLength check above cannot see it). Deterministic under the
            // test server as well as Kestrel. Rejects with 413 before any model work is scheduled.
            if (await ExceedsBodyLimitAsync(context, _options.MaxRequestBytes))
            {
                await WriteProblem(context, HttpStatusCode.RequestEntityTooLarge,
                    "anonymous_request_too_large",
                    $"Request body exceeds the anonymous limit of {_options.MaxRequestBytes} bytes.");
                return;
            }

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

    /// <summary>
    /// Reads the request body through a length counter and returns true as soon as it exceeds
    /// <paramref name="maxBytes"/>. Buffering is enabled first and the stream is rewound afterwards
    /// so downstream model binding re-reads the same body. This is the deterministic defense against
    /// a chunked / unknown-length body that omits <c>Content-Length</c>.
    /// </summary>
    private static async Task<bool> ExceedsBodyLimitAsync(HttpContext context, long maxBytes)
    {
        context.Request.EnableBuffering();
        Stream body = context.Request.Body;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        long total = 0;
        bool exceeded = false;
        try
        {
            int read;
            while ((read = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), context.RequestAborted)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    exceeded = true;
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (body.CanSeek)
            {
                body.Position = 0;
            }
        }

        return exceeded;
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

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace RetailPulse.Api.Security;

/// <summary>
/// Centralised rate-limiter wiring for the API.
///
/// This lives in its own extension method rather than inline in <c>Program.cs</c> so the
/// limits are reachable from tests. <c>Program.cs</c> uses top-level statements and boots
/// the full application graph (Azure OpenAI, tenant config, durable stores), which a unit
/// test cannot stand up. When the limits were configured inline, the only thing a test
/// could do was restate the expected numbers in its own fixture — which passes even when
/// the production limits are wrong. Registering through this seam lets
/// <c>RateLimitingConfigTests</c> exercise the REAL policies behaviourally.
///
/// Do not inline these values back into <c>Program.cs</c>.
/// </summary>
public static class RateLimitingSetup
{
    /// <summary>Permit limit for AI-intensive routes (chat, streaming chat, council, escalate).</summary>
    public const int StrictPermitLimit = 10;

    /// <summary>Permit limit for the file/large-body upload route.</summary>
    public const int UploadPermitLimit = 5;

    /// <summary>Permit limit for state-changing endpoints.</summary>
    public const int ModeratePermitLimit = 30;

    /// <summary>Permit limit for read-only reporting endpoints.</summary>
    public const int RelaxedPermitLimit = 100;

    /// <summary>Conservative default for the global anonymous bootstrap window.</summary>
    public const int AnonymousBootstrapDefaultPermitLimit = 5;

    /// <summary>Default permit limit for the GitHub BFF login-start window.</summary>
    public const int GitHubStartDefaultPermitLimit = 10;

    /// <summary>Default permit limit for the GitHub BFF code-exchange window.</summary>
    public const int GitHubExchangeDefaultPermitLimit = 20;

    /// <summary>The window shared by every policy.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>The four always-on core policies, in ascending order of permitted volume.</summary>
    public static readonly IReadOnlyDictionary<string, int> CorePolicies =
        new Dictionary<string, int>
        {
            ["upload"] = UploadPermitLimit,
            ["strict"] = StrictPermitLimit,
            ["moderate"] = ModeratePermitLimit,
            ["relaxed"] = RelaxedPermitLimit,
        };

    /// <summary>
    /// Registers every Retail Pulse rate-limiting policy. Behaviour is identical to the
    /// previous inline configuration in <c>Program.cs</c>.
    /// </summary>
    public static IServiceCollection AddRetailPulseRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            foreach ((string policyName, int permitLimit) in CorePolicies)
            {
                options.AddFixedWindowLimiter(policyName, opt =>
                {
                    opt.PermitLimit = permitLimit;
                    opt.Window = Window;
                    opt.QueueLimit = 0;
                });
            }

            // Rate limiter for the unauthenticated Anonymous bootstrap endpoint. Always registered so
            // the limiter graph is stable and testable; only used when Authentication:Mode=Anonymous
            // maps the bootstrap route.
            //
            // IMPORTANT (ACA ingress reality): when hosted behind Azure Container Apps, every request
            // arrives from the ingress/proxy, so HttpContext.Connection.RemoteIpAddress is the proxy's
            // IP, NOT the client's — a per-IP partition would therefore collapse to a single global
            // bucket anyway. We do NOT trust X-Forwarded-For to recover the client IP: ACA does not
            // give us a cryptographically verifiable client-IP header, and an attacker can forge XFF
            // to shard around a per-IP limit. So bootstrap is intentionally a single GLOBAL,
            // conservative fixed window — it caps total anonymous session minting per minute for the
            // whole replica and cannot be bypassed by header spoofing. Fine-grained abuse control is
            // enforced AFTER bootstrap by the per-subject (immutable, server-minted sub) limits in
            // AnonymousGuardMiddleware, which is the primary control. Note: this window is
            // replica-local; hosted Anonymous runs at maxReplicas=1.
            //
            // Config key: Anonymous:Bootstrap:GlobalPerMinute (conservative default 5). The legacy
            // Anonymous:Bootstrap:PerIpPerMinute key is still honoured as a fallback for backward
            // compatibility — it was never actually per-IP behind ACA, hence the rename.
            options.AddPolicy("anonymous-bootstrap", _ =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: "anonymous-bootstrap-global",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = configuration.GetValue("Anonymous:Bootstrap:GlobalPerMinute",
                            configuration.GetValue("Anonymous:Bootstrap:PerIpPerMinute",
                                AnonymousBootstrapDefaultPermitLimit)),
                        Window = Window,
                        QueueLimit = 0,
                    }));

            // Rate limiters for the GitHub confidential OAuth BFF endpoints. Always registered so the
            // limiter graph is stable and testable; only used when Authentication:Mode=GitHub maps the
            // endpoints. Behind ACA the client IP is the proxy's and X-Forwarded-For is forgeable, so
            // these are single GLOBAL per-replica fixed windows (not per-IP) that cap login-flow abuse
            // (state minting and code redemption) without being bypassable by header spoofing.
            // Replica-local; hosted GitHub runs at maxReplicas=1.
            // Config keys: GitHub:RateLimits:StartPerMinute / ExchangePerMinute.
            options.AddPolicy("github-start", _ =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: "github-start-global",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = configuration.GetValue("GitHub:RateLimits:StartPerMinute",
                            GitHubStartDefaultPermitLimit),
                        Window = Window,
                        QueueLimit = 0,
                    }));

            options.AddPolicy("github-exchange", _ =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: "github-exchange-global",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = configuration.GetValue("GitHub:RateLimits:ExchangePerMinute",
                            GitHubExchangeDefaultPermitLimit),
                        Window = Window,
                        QueueLimit = 0,
                    }));
        });

        return services;
    }
}

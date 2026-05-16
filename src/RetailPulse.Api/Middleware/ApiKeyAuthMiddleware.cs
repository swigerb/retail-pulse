using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace RetailPulse.Api.Middleware;

/// <summary>
/// Optional API-key gate for the demo. Disabled by default.
///
/// This middleware is intentionally minimal — it is here to demonstrate the
/// pattern, not to be a substitute for real authentication. Production
/// deployments should replace this with JWT bearer authentication and
/// per-route authorization policies.
///
/// Configuration:
///   ApiKey:Enabled = true|false   (default: false)
///   ApiKey:Header  = X-Api-Key    (default)
///   ApiKey:Value   = &lt;secret&gt;     (required when Enabled=true)
/// </summary>
public class ApiKeyAuthMiddleware
{
    private const string _defaultHeaderName = "X-Api-Key";

    private readonly RequestDelegate _next;
    private readonly bool _enabled;
    private readonly string _headerName;
    private readonly byte[]? _expectedKeyBytes;
    private readonly ILogger<ApiKeyAuthMiddleware> _logger;

    public ApiKeyAuthMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        ILogger<ApiKeyAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _enabled = configuration.GetValue("ApiKey:Enabled", false);
        _headerName = configuration["ApiKey:Header"] ?? _defaultHeaderName;
        string? expectedKey = configuration["ApiKey:Value"];
        _expectedKeyBytes = string.IsNullOrWhiteSpace(expectedKey)
            ? null
            : Encoding.UTF8.GetBytes(expectedKey);

        if (_enabled && _expectedKeyBytes is null)
        {
            _logger.LogWarning(
                "ApiKey:Enabled=true but ApiKey:Value is not configured. All /api requests will be rejected.");
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_enabled || !context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        if (_expectedKeyBytes is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("API key gate is enabled but no key is configured.");
            return;
        }

        if (!context.Request.Headers.TryGetValue(_headerName, out StringValues provided)
            || !KeysMatch(provided.ToString(), _expectedKeyBytes))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Missing or invalid API key.");
            return;
        }

        await _next(context);
    }

    // Constant-time comparison to prevent timing-based key recovery attacks.
    // FixedTimeEquals requires equal-length spans, so reject length mismatches
    // up front (length itself is not secret).
    private static bool KeysMatch(string provided, byte[] expectedBytes)
    {
        if (string.IsNullOrEmpty(provided))
            return false;

        byte[] providedBytes = Encoding.UTF8.GetBytes(provided);
        return providedBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}

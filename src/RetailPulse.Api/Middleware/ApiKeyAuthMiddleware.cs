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
    private const string DefaultHeaderName = "X-Api-Key";

    private readonly RequestDelegate _next;
    private readonly bool _enabled;
    private readonly string _headerName;
    private readonly string? _expectedKey;
    private readonly ILogger<ApiKeyAuthMiddleware> _logger;

    public ApiKeyAuthMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        ILogger<ApiKeyAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _enabled = configuration.GetValue("ApiKey:Enabled", false);
        _headerName = configuration["ApiKey:Header"] ?? DefaultHeaderName;
        _expectedKey = configuration["ApiKey:Value"];

        if (_enabled && string.IsNullOrWhiteSpace(_expectedKey))
        {
            _logger.LogWarning(
                "ApiKey:Enabled=true but ApiKey:Value is not configured. All /api/chat requests will be rejected.");
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_enabled || !context.Request.Path.StartsWithSegments("/api/chat"))
        {
            await _next(context);
            return;
        }

        if (string.IsNullOrWhiteSpace(_expectedKey))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("API key gate is enabled but no key is configured.");
            return;
        }

        if (!context.Request.Headers.TryGetValue(_headerName, out var provided)
            || !string.Equals(provided.ToString(), _expectedKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Missing or invalid API key.");
            return;
        }

        await _next(context);
    }
}

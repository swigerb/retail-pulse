namespace RetailPulse.Api.Middleware;

/// <summary>
/// Extracts or generates a correlation ID for every request and pushes it into
/// the logger scope so all downstream log entries include it.
/// </summary>
public class CorrelationIdMiddleware
{
    private const string _headerName = "X-Correlation-ID";
    private const string _itemKey = "CorrelationId";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[_headerName].FirstOrDefault()
            ?? Guid.NewGuid().ToString("D");

        context.Items[_itemKey] = correlationId;
        context.Response.Headers[_headerName] = correlationId;

        using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await _next(context);
        }
    }
}

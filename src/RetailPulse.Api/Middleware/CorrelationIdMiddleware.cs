using RetailPulse.Api.OpenAI.Gateway;

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

    private readonly bool _maskClientVisible;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger, IConfiguration? configuration = null)
    {
        _next = next;
        _logger = logger;
        _maskClientVisible = configuration is not null && RegulatedRetailProfile.Resolve(configuration).Enabled;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string correlationId = context.Request.Headers[_headerName].FirstOrDefault()
            ?? Guid.NewGuid().ToString("D");

        context.Items[_itemKey] = correlationId;
        // Regulated-retail mode (#337): clients see a masked id; logs keep the full value.
        context.Response.Headers[_headerName] = _maskClientVisible
            ? ClientIdentifierMasker.Mask(correlationId)
            : correlationId;

        using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await _next(context);
        }
    }
}

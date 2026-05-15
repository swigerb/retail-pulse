using System.Net;
using System.Text.Json;
using RetailPulse.Api.Resilience;

namespace RetailPulse.Api.Middleware;

/// <summary>
/// Global exception handler that returns RFC 7807 Problem Details
/// and enriches structured logs with correlation/classification metadata.
/// </summary>
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception ex)
    {
        var correlationId = context.TraceIdentifier;
        var category = ErrorClassifier.Classify(ex);
        var path = context.Request.Path.Value ?? "/";

        _logger.LogError(ex,
            "Unhandled exception | CorrelationId={CorrelationId} Category={ErrorCategory} ExceptionType={ExceptionType} Path={RequestPath}",
            correlationId, category, ex.GetType().Name, path);

        var statusCode = MapToStatusCode(category, ex);

        var problemDetails = new
        {
            type = $"https://httpstatuses.io/{(int)statusCode}",
            title = GetTitle(category),
            status = (int)statusCode,
            detail = GetDetail(category, ex),
            instance = path,
            correlationId,
            errorCategory = category.ToString()
        };

        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(problemDetails, JsonOptions));
    }

    private static HttpStatusCode MapToStatusCode(ErrorCategory category, Exception ex) => category switch
    {
        ErrorCategory.Transient => HttpStatusCode.ServiceUnavailable,
        ErrorCategory.User => GetUserStatusCode(ex),
        ErrorCategory.External => HttpStatusCode.BadGateway,
        _ => HttpStatusCode.InternalServerError
    };

    private static HttpStatusCode GetUserStatusCode(Exception ex) => ex switch
    {
        ArgumentException => HttpStatusCode.BadRequest,
        FormatException => HttpStatusCode.BadRequest,
        _ => HttpStatusCode.BadRequest
    };

    private static string GetTitle(ErrorCategory category) => category switch
    {
        ErrorCategory.Transient => "Service Temporarily Unavailable",
        ErrorCategory.User => "Invalid Request",
        ErrorCategory.External => "External Dependency Failure",
        _ => "Internal Server Error"
    };

    private static string GetDetail(ErrorCategory category, Exception ex) => category switch
    {
        ErrorCategory.User => ex.Message,
        ErrorCategory.Transient => "The service is temporarily unavailable. Please retry shortly.",
        ErrorCategory.External => "An external dependency is not responding. The request could not be completed.",
        _ => "An unexpected error occurred. Use the correlationId to report this issue."
    };
}

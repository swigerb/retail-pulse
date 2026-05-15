using System.Net;
using System.Net.Sockets;
using Polly.CircuitBreaker;

namespace RetailPulse.Api.Resilience;

/// <summary>
/// Categorizes exceptions for structured logging, retry decisions, and alerting.
/// </summary>
public enum ErrorCategory
{
    /// <summary>Temporary failures that may succeed on retry (timeouts, 429, 503).</summary>
    Transient,

    /// <summary>Client errors caused by invalid input (validation, 400, 404).</summary>
    User,

    /// <summary>Internal system errors (NullRef, InvalidOperation, unhandled).</summary>
    System,

    /// <summary>Failures from external dependencies (MCP server, OpenAI).</summary>
    External
}

public static class ErrorClassifier
{
    public static ErrorCategory Classify(Exception ex) => ex switch
    {
        // Circuit breaker open → the external service is known to be down
        BrokenCircuitException => ErrorCategory.External,

        // HTTP failures — classify by status code
        HttpRequestException httpEx when httpEx.StatusCode.HasValue =>
            ClassifyStatusCode(httpEx.StatusCode.Value),

        // Timeouts are transient
        HttpRequestException httpEx when IsTimeout(httpEx) => ErrorCategory.Transient,
        TaskCanceledException => ErrorCategory.Transient,
        OperationCanceledException => ErrorCategory.Transient,
        TimeoutException => ErrorCategory.Transient,
        SocketException => ErrorCategory.Transient,

        // Generic HTTP failures without status code → external dependency issue
        HttpRequestException => ErrorCategory.External,

        // Validation / argument errors → user error
        ArgumentException => ErrorCategory.User,
        FormatException => ErrorCategory.User,

        // Everything else → system error
        _ => ErrorCategory.System
    };

    private static ErrorCategory ClassifyStatusCode(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.TooManyRequests => ErrorCategory.Transient,        // 429
        HttpStatusCode.RequestTimeout => ErrorCategory.Transient,          // 408
        HttpStatusCode.ServiceUnavailable => ErrorCategory.Transient,      // 503
        HttpStatusCode.GatewayTimeout => ErrorCategory.Transient,          // 504
        HttpStatusCode.BadGateway => ErrorCategory.Transient,              // 502

        HttpStatusCode.BadRequest => ErrorCategory.User,                   // 400
        HttpStatusCode.NotFound => ErrorCategory.User,                     // 404
        HttpStatusCode.UnprocessableEntity => ErrorCategory.User,          // 422

        HttpStatusCode.Unauthorized => ErrorCategory.External,             // 401
        HttpStatusCode.Forbidden => ErrorCategory.External,                // 403
        HttpStatusCode.InternalServerError => ErrorCategory.External,      // 500

        _ => ErrorCategory.System
    };

    private static bool IsTimeout(HttpRequestException ex) =>
        ex.InnerException is TimeoutException or TaskCanceledException or SocketException;
}

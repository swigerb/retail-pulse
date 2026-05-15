using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Middleware;

namespace RetailPulse.Tests.Chaos;

/// <summary>
/// Tests for ExceptionHandlingMiddleware — verifies RFC 7807 Problem Details output.
/// </summary>
public class ExceptionHandlingMiddlewareTests
{
    [Fact]
    public async Task Middleware_UnhandledException_ReturnsProblemDetails()
    {
        var middleware = new ExceptionHandlingMiddleware(
            next: _ => throw new InvalidOperationException("Test error"),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(500);
        context.Response.ContentType.Should().Be("application/problem+json");

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        var problem = JsonDocument.Parse(body);
        problem.RootElement.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
        problem.RootElement.GetProperty("status").GetInt32().Should().Be(500);
        problem.RootElement.GetProperty("errorCategory").GetString().Should().Be("System");
    }

    [Fact]
    public async Task Middleware_ArgumentException_Returns400()
    {
        var middleware = new ExceptionHandlingMiddleware(
            next: _ => throw new ArgumentException("Invalid input"),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(400);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        var problem = JsonDocument.Parse(body);
        problem.RootElement.GetProperty("errorCategory").GetString().Should().Be("User");
        problem.RootElement.GetProperty("detail").GetString().Should().Contain("Invalid input");
    }

    [Fact]
    public async Task Middleware_HttpRequestException503_Returns503()
    {
        var middleware = new ExceptionHandlingMiddleware(
            next: _ => throw new HttpRequestException("Service down", null, HttpStatusCode.ServiceUnavailable),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(503);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        var problem = JsonDocument.Parse(body);
        problem.RootElement.GetProperty("errorCategory").GetString().Should().Be("Transient");
    }

    [Fact]
    public async Task Middleware_NoException_PassesThrough()
    {
        var middleware = new ExceptionHandlingMiddleware(
            next: ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; },
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task Middleware_IncludesCorrelationId()
    {
        var middleware = new ExceptionHandlingMiddleware(
            next: _ => throw new Exception("boom"),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance);

        var context = new DefaultHttpContext
        {
            TraceIdentifier = "test-correlation-123"
        };
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("test-correlation-123");
    }
}

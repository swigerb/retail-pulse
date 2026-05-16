using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Middleware;

namespace RetailPulse.Tests.Observability;

public class CorrelationIdMiddlewareTests
{
    private readonly Mock<ILogger<CorrelationIdMiddleware>> _logger = new();

    [Fact]
    public async Task InvokeAsync_WithExistingHeader_UsesProvidedCorrelationId()
    {
        string expectedId = "test-correlation-123";
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Correlation-ID"] = expectedId;

        var middleware = new CorrelationIdMiddleware(
            next: _ => Task.CompletedTask,
            _logger.Object);

        await middleware.InvokeAsync(context);

        context.Items["CorrelationId"].Should().Be(expectedId);
        context.Response.Headers["X-Correlation-ID"].ToString().Should().Be(expectedId);
    }

    [Fact]
    public async Task InvokeAsync_WithoutHeader_GeneratesNewGuid()
    {
        var context = new DefaultHttpContext();

        var middleware = new CorrelationIdMiddleware(
            next: _ => Task.CompletedTask,
            _logger.Object);

        await middleware.InvokeAsync(context);

        string? correlationId = context.Items["CorrelationId"] as string;
        correlationId.Should().NotBeNullOrEmpty();
        Guid.TryParse(correlationId, out _).Should().BeTrue();
        context.Response.Headers["X-Correlation-ID"].ToString().Should().Be(correlationId);
    }

    [Fact]
    public async Task InvokeAsync_CallsNextMiddleware()
    {
        var context = new DefaultHttpContext();
        bool nextCalled = false;

        var middleware = new CorrelationIdMiddleware(
            next: _ => { nextCalled = true; return Task.CompletedTask; },
            _logger.Object);

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }
}

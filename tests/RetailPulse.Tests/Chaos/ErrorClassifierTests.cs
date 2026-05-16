using System.Net;
using FluentAssertions;
using RetailPulse.Api.Resilience;

namespace RetailPulse.Tests.Chaos;

/// <summary>
/// Unit tests for ErrorClassifier covering all exception categories.
/// </summary>
public class ErrorClassifierTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public void Classify_TransientHttpStatus_ReturnsTransient(HttpStatusCode status)
    {
        var ex = new HttpRequestException("fail", null, status);
        ErrorClassifier.Classify(ex).Should().Be(ErrorCategory.Transient);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public void Classify_UserHttpStatus_ReturnsUser(HttpStatusCode status)
    {
        var ex = new HttpRequestException("fail", null, status);
        ErrorClassifier.Classify(ex).Should().Be(ErrorCategory.User);
    }

    [Fact]
    public void Classify_TimeoutException_ReturnsTransient() => ErrorClassifier.Classify(new TimeoutException()).Should().Be(ErrorCategory.Transient);

    [Fact]
    public void Classify_SocketException_ReturnsTransient() => ErrorClassifier.Classify(new System.Net.Sockets.SocketException()).Should().Be(ErrorCategory.Transient);

    [Fact]
    public void Classify_FormatException_ReturnsUser() => ErrorClassifier.Classify(new FormatException("bad format")).Should().Be(ErrorCategory.User);

    [Fact]
    public void Classify_InvalidOperationException_ReturnsSystem() => ErrorClassifier.Classify(new InvalidOperationException()).Should().Be(ErrorCategory.System);

    [Fact]
    public void Classify_HttpRequestException_401_ReturnsExternal()
    {
        var ex = new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized);
        ErrorClassifier.Classify(ex).Should().Be(ErrorCategory.External);
    }

    [Fact]
    public void Classify_HttpRequestException_500_ReturnsExternal()
    {
        var ex = new HttpRequestException("internal error", null, HttpStatusCode.InternalServerError);
        ErrorClassifier.Classify(ex).Should().Be(ErrorCategory.External);
    }
}

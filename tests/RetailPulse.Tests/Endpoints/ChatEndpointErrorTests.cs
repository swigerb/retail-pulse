using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Tests.Endpoints;

/// <summary>
/// HTTP-level error-path tests for <c>/api/chat</c> and <c>/api/chat/stream</c>.
/// <para>
/// Mirrors the exact try/catch shape from <c>ChatEndpoints.cs</c> (commit e1d7a46)
/// in a lightweight <see cref="TestServer"/>. The production endpoint requires
/// Azure credentials at startup so it cannot be hosted with
/// <c>WebApplicationFactory&lt;Program&gt;</c> — instead, this test reproduces the
/// production catch blocks verbatim and exercises them with a <see cref="Mock"/>
/// <see cref="IAgentRouter"/> that throws the relevant exception.
/// </para>
/// <para>
/// If the production endpoint's error contract changes (status code, JSON shape,
/// error message, code field), both the test handler below and the production
/// endpoint must be updated together. The test handler is intentionally a
/// near-line-for-line copy of the production catch logic so drift is obvious in
/// code review.
/// </para>
/// </summary>
public class ChatEndpointErrorTests : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;
    private readonly Mock<IAgentRouter> _routerMock;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ChatEndpointErrorTests()
    {
        _routerMock = new Mock<IAgentRouter>();

        IHostBuilder builder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
                    services.AddSingleton(_routerMock.Object);
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        // /api/chat — mirrors the production catch blocks from
                        // ChatEndpoints.cs MapChatEndpoints (lines ~472-532).
                        endpoints.MapPost("/api/chat",
                            async (HttpContext ctx, ChatRequest request, IAgentRouter router, ILoggerFactory lf) =>
                        {
                            ILogger logger = lf.CreateLogger("ChatEndpointErrorTests");
                            CancellationToken clientCt = ctx.RequestAborted;
                            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(clientCt);
                            // 60s firm timeout — same as production
                            requestCts.CancelAfter(TimeSpan.FromSeconds(60));
                            CancellationToken ct = requestCts.Token;
                            try
                            {
                                // The mocked router throws the exception under test —
                                // simulating an APIM failure during router classification.
                                _ = await router.RouteAsync(request.Message, null, null, null, ct);

                                // Never reached in error-path tests, but keep the structure
                                // honest by returning a 200 with a valid ChatResponse.
                                return Results.Ok(new ChatResponse(
                                    Reply: "ok",
                                    SessionId: request.SessionId ?? "test",
                                    Spans: [],
                                    Charts: null,
                                    TotalDurationMs: 0,
                                    Routing: new RoutingInfo("general", "General", "general", 0.9, 1)));
                            }
                            catch (OperationCanceledException) when (clientCt.IsCancellationRequested)
                            {
                                return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
                            }
                            catch (OperationCanceledException)
                            {
                                logger.LogWarning("Chat request timed out");
                                return Results.Json(
                                    new
                                    {
                                        error = "The AI service took too long to respond. Please try again — if it persists, try a simpler question first.",
                                        code = "request_timeout"
                                    },
                                    statusCode: StatusCodes.Status504GatewayTimeout);
                            }
                            catch (ClientResultException ex) when (ex.Status == 429)
                            {
                                logger.LogWarning(ex, "Rate-limited (429)");
                                return Results.Json(
                                    new
                                    {
                                        error = "The AI service is temporarily rate-limited. Please wait a moment and try again.",
                                        code = "rate_limited"
                                    },
                                    statusCode: StatusCodes.Status429TooManyRequests);
                            }
                            catch (ClientResultException ex)
                            {
                                logger.LogError(ex, "ClientResultException (HTTP {Status})", ex.Status);
                                int statusCode = ex.Status is >= 400 and < 600
                                    ? ex.Status
                                    : StatusCodes.Status503ServiceUnavailable;
                                return Results.Json(
                                    new
                                    {
                                        error = "The AI service encountered an error. Please try again shortly.",
                                        code = "ai_service_error"
                                    },
                                    statusCode: statusCode);
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "Unhandled error");
                                return Results.Json(
                                    new
                                    {
                                        error = "The AI service is temporarily unavailable. Please try again shortly.",
                                        code = "service_unavailable"
                                    },
                                    statusCode: StatusCodes.Status503ServiceUnavailable);
                            }
                        });

                        // /api/chat/stream — mirrors the production catch blocks from
                        // ChatEndpoints.cs MapChatEndpoints (lines ~641-693). Streaming
                        // endpoint has slightly different (shorter) error messages.
                        endpoints.MapPost("/api/chat/stream",
                            async (HttpContext ctx, ChatRequest request, IAgentRouter router, ILoggerFactory lf) =>
                        {
                            ILogger logger = lf.CreateLogger("ChatEndpointErrorTests.Stream");
                            CancellationToken clientCt = ctx.RequestAborted;
                            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(clientCt);
                            requestCts.CancelAfter(TimeSpan.FromSeconds(60));
                            CancellationToken ct = requestCts.Token;
                            try
                            {
                                _ = await router.RouteAsync(request.Message, null, null, null, ct);
                                return Results.Ok(new ChatResponse(
                                    Reply: "ok",
                                    SessionId: request.SessionId ?? "test",
                                    Spans: []));
                            }
                            catch (OperationCanceledException) when (clientCt.IsCancellationRequested)
                            {
                                return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
                            }
                            catch (OperationCanceledException)
                            {
                                logger.LogWarning("Streaming chat timed out");
                                return Results.Json(
                                    new { error = "The AI service took too long to respond.", code = "request_timeout" },
                                    statusCode: StatusCodes.Status504GatewayTimeout);
                            }
                            catch (ClientResultException ex) when (ex.Status == 429)
                            {
                                logger.LogWarning(ex, "Rate-limited (429) streaming");
                                return Results.Json(
                                    new { error = "The AI service is temporarily rate-limited. Please wait a moment and try again.", code = "rate_limited" },
                                    statusCode: StatusCodes.Status429TooManyRequests);
                            }
                            catch (ClientResultException ex)
                            {
                                logger.LogError(ex, "ClientResultException (HTTP {Status}) streaming", ex.Status);
                                int statusCode = ex.Status is >= 400 and < 600
                                    ? ex.Status
                                    : StatusCodes.Status503ServiceUnavailable;
                                return Results.Json(
                                    new { error = "The AI service encountered an error. Please try again shortly.", code = "ai_service_error" },
                                    statusCode: statusCode);
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "Streaming chat error");
                                return Results.Json(
                                    new { error = "The AI service is temporarily unavailable.", code = "service_unavailable" },
                                    statusCode: StatusCodes.Status503ServiceUnavailable);
                            }
                        });
                    });
                });
            });

        _host = builder.Build();
        _host.Start();
        _client = _host.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    #region /api/chat — 429 Rate-Limited

    [Fact]
    public async Task Chat_WhenRouterThrows429_Returns429WithRateLimitedCode()
    {
        SetupRouterThrows(BuildRateLimitException());

        HttpResponseMessage resp = await PostChatAsync("/api/chat");

        resp.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        ErrorResponse body = await ReadErrorAsync(resp);
        body.Code.Should().Be("rate_limited");
        body.Error.Should().Contain("rate-limited");
    }

    [Fact]
    public async Task Chat_429Response_DoesNotIncludeRoutingMetadata()
    {
        SetupRouterThrows(BuildRateLimitException());

        HttpResponseMessage resp = await PostChatAsync("/api/chat");
        string raw = await resp.Content.ReadAsStringAsync();

        // Error responses must NOT leak routing info (confidence, agent name,
        // intent) since there was no successful classification.
        raw.Should().NotContain("\"routing\"", "error responses must not include routing metadata");
        raw.Should().NotContain("confidence");
        raw.Should().NotContain("agentName");
    }

    #endregion

    #region /api/chat/stream — 429 Rate-Limited

    [Fact]
    public async Task ChatStream_WhenRouterThrows429_Returns429WithRateLimitedCode()
    {
        SetupRouterThrows(BuildRateLimitException());

        HttpResponseMessage resp = await PostChatAsync("/api/chat/stream");

        resp.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        ErrorResponse body = await ReadErrorAsync(resp);
        body.Code.Should().Be("rate_limited");
        body.Error.Should().Contain("rate-limited");
    }

    [Fact]
    public async Task ChatStream_429Response_DoesNotIncludeRoutingMetadata()
    {
        SetupRouterThrows(BuildRateLimitException());

        HttpResponseMessage resp = await PostChatAsync("/api/chat/stream");
        string raw = await resp.Content.ReadAsStringAsync();

        raw.Should().NotContain("\"routing\"");
        raw.Should().NotContain("confidence");
    }

    #endregion

    #region Timeout — 504 Gateway Timeout

    [Fact]
    public async Task Chat_WhenRouterTimesOut_Returns504WithRequestTimeoutCode()
    {
        // Simulate a server-side timeout: the linked CTS cancels our token,
        // and the router throws TaskCanceledException — exactly what
        // OperationCanceledException catches in production.
        _routerMock
            .Setup(r => r.RouteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ChatHistoryMessage>?>(),
                It.IsAny<UserContext?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("Simulated server-side timeout."));

        HttpResponseMessage resp = await PostChatAsync("/api/chat");

        resp.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        ErrorResponse body = await ReadErrorAsync(resp);
        body.Code.Should().Be("request_timeout");
        body.Error.Should().Contain("too long");
    }

    [Fact]
    public async Task ChatStream_WhenRouterTimesOut_Returns504WithRequestTimeoutCode()
    {
        _routerMock
            .Setup(r => r.RouteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ChatHistoryMessage>?>(),
                It.IsAny<UserContext?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("Simulated server-side timeout."));

        HttpResponseMessage resp = await PostChatAsync("/api/chat/stream");

        resp.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        ErrorResponse body = await ReadErrorAsync(resp);
        body.Code.Should().Be("request_timeout");
    }

    [Fact]
    public async Task Chat_TimeoutResponse_DoesNotIncludeRoutingMetadata()
    {
        _routerMock
            .Setup(r => r.RouteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ChatHistoryMessage>?>(),
                It.IsAny<UserContext?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("timeout"));

        HttpResponseMessage resp = await PostChatAsync("/api/chat");
        string raw = await resp.Content.ReadAsStringAsync();

        raw.Should().NotContain("\"routing\"");
    }

    #endregion

    #region Generic Backend Errors — 503 Service Unavailable

    [Fact]
    public async Task Chat_WhenRouterThrowsGenericException_Returns503ServiceUnavailable()
    {
        _routerMock
            .Setup(r => r.RouteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ChatHistoryMessage>?>(),
                It.IsAny<UserContext?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("backend exploded"));

        HttpResponseMessage resp = await PostChatAsync("/api/chat");

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        ErrorResponse body = await ReadErrorAsync(resp);
        body.Code.Should().Be("service_unavailable");
        body.Error.Should().Contain("temporarily unavailable");
    }

    [Fact]
    public async Task ChatStream_WhenRouterThrowsGenericException_Returns503ServiceUnavailable()
    {
        _routerMock
            .Setup(r => r.RouteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ChatHistoryMessage>?>(),
                It.IsAny<UserContext?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("backend exploded"));

        HttpResponseMessage resp = await PostChatAsync("/api/chat/stream");

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        ErrorResponse body = await ReadErrorAsync(resp);
        body.Code.Should().Be("service_unavailable");
    }

    [Fact]
    public async Task Chat_GenericErrorResponse_DoesNotIncludeRoutingMetadata()
    {
        _routerMock
            .Setup(r => r.RouteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ChatHistoryMessage>?>(),
                It.IsAny<UserContext?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        HttpResponseMessage resp = await PostChatAsync("/api/chat");
        string raw = await resp.Content.ReadAsStringAsync();

        raw.Should().NotContain("\"routing\"");
        raw.Should().NotContain("confidence");
    }

    #endregion

    #region ClientResultException with Non-429 Status — Forwards Status

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task Chat_WhenClientResultException5xx_ForwardsStatusCodeWithAiServiceErrorCode(int upstreamStatus)
    {
        SetupRouterThrows(BuildClientResultException(upstreamStatus));

        HttpResponseMessage resp = await PostChatAsync("/api/chat");

        ((int)resp.StatusCode).Should().Be(upstreamStatus,
            "the endpoint should forward the upstream APIM status instead of always returning 503");
        ErrorResponse body = await ReadErrorAsync(resp);
        body.Code.Should().Be("ai_service_error");
    }

    [Fact]
    public async Task Chat_WhenClientResultExceptionOutOfRange_FallsBackTo503()
    {
        // ex.Status outside [400, 600) should fall back to 503 per production logic.
        SetupRouterThrows(BuildClientResultException(0));

        HttpResponseMessage resp = await PostChatAsync("/api/chat");

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        ErrorResponse body = await ReadErrorAsync(resp);
        body.Code.Should().Be("ai_service_error");
    }

    #endregion

    #region Error Response Shape Contract

    [Fact]
    public async Task Chat_AllErrorResponses_HaveErrorAndCodeFields()
    {
        // Walk through every documented error contract and verify the JSON
        // payload exposes the two stable fields the frontend depends on.
        (Exception Thrown, HttpStatusCode Expected, string ExpectedCode)[] cases =
        [
            (BuildRateLimitException(),                HttpStatusCode.TooManyRequests,  "rate_limited"),
            (BuildClientResultException(502),          HttpStatusCode.BadGateway,       "ai_service_error"),
            (new TaskCanceledException("timeout"),     HttpStatusCode.GatewayTimeout,   "request_timeout"),
            (new InvalidOperationException("boom"),    HttpStatusCode.ServiceUnavailable, "service_unavailable"),
        ];

        foreach ((Exception thrown, HttpStatusCode expected, string expectedCode) in cases)
        {
            SetupRouterThrows(thrown);

            HttpResponseMessage resp = await PostChatAsync("/api/chat");

            resp.StatusCode.Should().Be(expected, $"thrown: {thrown.GetType().Name}");
            ErrorResponse body = await ReadErrorAsync(resp);
            body.Code.Should().Be(expectedCode);
            body.Error.Should().NotBeNullOrWhiteSpace();
        }
    }

    #endregion

    #region Helpers

    private void SetupRouterThrows(Exception ex)
    {
        _routerMock.Reset();
        _routerMock
            .Setup(r => r.RouteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ChatHistoryMessage>?>(),
                It.IsAny<UserContext?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(ex);
    }

    private Task<HttpResponseMessage> PostChatAsync(string path) =>
        _client.PostAsJsonAsync(path, new ChatRequest(Message: "hello", SessionId: "err-test"));

    private static async Task<ErrorResponse> ReadErrorAsync(HttpResponseMessage resp)
    {
        string raw = await resp.Content.ReadAsStringAsync();
        ErrorResponse? parsed = JsonSerializer.Deserialize<ErrorResponse>(raw, JsonOptions);
        parsed.Should().NotBeNull("error responses must always be JSON with error + code fields");
        return parsed;
    }

    private static ClientResultException BuildRateLimitException() =>
        BuildClientResultException(429);

    private static ClientResultException BuildClientResultException(int status)
    {
        var response = new Mock<PipelineResponse>();
        response.SetupGet(x => x.Status).Returns(status);
        response.SetupGet(x => x.ReasonPhrase).Returns("Error");
        response.SetupProperty(x => x.ContentStream, new MemoryStream());
        response.Setup(x => x.Dispose());
        return new ClientResultException($"HTTP {status}", response.Object, null);
    }

    private sealed record ErrorResponse(string Error, string Code);

    #endregion
}

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Endpoints;
using RetailPulse.Api.Explainability;
using RetailPulse.Api.Observability;
using RetailPulse.Contracts.Caching;
using RetailPulse.Contracts.Observability;
using RetailPulse.Contracts.Tracing;

namespace RetailPulse.Tests.Endpoints;

/// <summary>
/// Contract-level HTTP tests for the observability export/preview/audit surface.
/// <para>
/// Unlike the shape-only tests, these host the <b>real</b>
/// <see cref="ObservabilityEndpoints.MapObservabilityEndpoints"/> in a
/// <see cref="TestServer"/> with stub auth + no-op rate limiter policies, so the
/// production endpoint code (DTO projection, <c>Results.File</c> headers, 404
/// handling, body/query format selection) is exercised against the actual wire
/// shapes the frontend consumes. No production endpoint logic is duplicated here.
/// </para>
/// </summary>
public sealed class ObservabilityExportEndpointTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services
            .AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, StubAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy("relaxed", _ => RateLimitPartition.GetNoLimiter("all"));
            options.AddPolicy("moderate", _ => RateLimitPartition.GetNoLimiter("all"));
        });

        builder.Services.AddSingleton(Options.Create(new ObservabilityOptions()));
        builder.Services.AddSingleton<ConversationExporter>();
        builder.Services.AddSingleton<IConversationExport>(sp => sp.GetRequiredService<ConversationExporter>());
        builder.Services.AddSingleton<IAuditLog, InMemoryAuditLog>();

        // Other services referenced by MapObservabilityEndpoints must be registered
        // so the minimal-API request-delegate factory treats them as services (not
        // inferred body params); these endpoints are not exercised by these tests.
        builder.Services.AddSingleton(new Mock<ITraceCollector>().Object);
        builder.Services.AddSingleton(new Mock<ICostTracker>().Object);
        builder.Services.AddSingleton(new Mock<IResponseCache>().Object);
        builder.Services.AddSingleton<ExplainabilityService>();

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.UseRateLimiter();
        _app.MapObservabilityEndpoints();

        // Seed a session and an audit entry using the real services.
        ConversationExporter exporter = _app.Services.GetRequiredService<ConversationExporter>();
        await exporter.TrackMessageAsync("sess-abc123", new TrackedMessage { Role = "user", Content = "How are sales?" });
        await exporter.TrackMessageAsync("sess-abc123", new TrackedMessage
        {
            Role = "assistant",
            Content = "Sales are up 12%.",
            AgentId = "demand-agent",
            DurationMs = 1234,
            Tokens = 200
        });

        IAuditLog audit = _app.Services.GetRequiredService<IAuditLog>();
        await audit.LogAsync(new AuditEntry(
            "audit-1", DateTime.UtcNow, "user-42", "demand-agent",
            "chat.DemandForecast", "How are sales?", "Sales are up 12%.",
            200, TimeSpan.FromMilliseconds(1234)));

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Preview_KnownSession_Returns200WithBoundedSlice()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/observability/export/sess-abc123/preview");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("sessionId").GetString().Should().Be("sess-abc123");
        doc.RootElement.GetProperty("totalMessages").GetInt32().Should().Be(2);
        JsonElement messages = doc.RootElement.GetProperty("messages");
        messages.GetArrayLength().Should().Be(2);
        messages[0].GetProperty("role").GetString().Should().Be("user");
        messages[0].GetProperty("content").GetString().Should().Be("How are sales?");
    }

    [Fact]
    public async Task Preview_UnknownSession_Returns404_NotSilentEmptySuccess()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/observability/export/nope/preview");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Export_Markdown_ReturnsRawMarkdownWithDownloadHeaders()
    {
        var body = new StringContent(/*lang=json,strict*/ "{\"format\":\"markdown\"}", Encoding.UTF8, "application/json");
        HttpResponseMessage response = await _client.PostAsync("/api/observability/export/sess-abc123", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Content.Headers.ContentDisposition.FileName.Should().EndWith(".md").And.Contain("session-");

        string content = await response.Content.ReadAsStringAsync();
        content.Should().StartWith("# Conversation Export", "the download must be raw Markdown, not a JSON envelope");
        content.Should().Contain("Sales are up 12%.");
    }

    [Fact]
    public async Task Export_Json_ReturnsParseableJsonWithDownloadHeaders()
    {
        var body = new StringContent(/*lang=json,strict*/ "{\"format\":\"json\"}", Encoding.UTF8, "application/json");
        HttpResponseMessage response = await _client.PostAsync("/api/observability/export/sess-abc123", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Content.Headers.ContentDisposition!.FileName.Should().EndWith(".json");

        // The JSON option must export genuine JSON, not Markdown.
        string content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        doc.RootElement.GetProperty("sessionId").GetString().Should().Be("sess-abc123");
        doc.RootElement.GetProperty("messages").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Export_FormatFromQueryString_IsHonored()
    {
        // No JSON body — format arrives only via the query string.
        HttpResponseMessage response = await _client.PostAsync("/api/observability/export/sess-abc123?format=json", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task Export_UnknownSession_Returns404()
    {
        var body = new StringContent(/*lang=json,strict*/ "{\"format\":\"markdown\"}", Encoding.UTF8, "application/json");
        HttpResponseMessage response = await _client.PostAsync("/api/observability/export/nope", body);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sessions_ExposeStartTimeAndTotalTokens()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/observability/export/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement session = doc.RootElement.EnumerateArray()
            .First(s => s.GetProperty("sessionId").GetString() == "sess-abc123");

        // Frontend expects camelCase startTime (not startedAt) and totalTokens.
        session.TryGetProperty("startTime", out JsonElement startTime).Should().BeTrue();
        DateTime.TryParse(startTime.GetString(), out _).Should().BeTrue("startTime must be a valid parseable date");
        session.GetProperty("totalTokens").GetInt32().Should().Be(200);
        session.GetProperty("messageCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Audit_ExposesIdsTokensAndNumericDuration()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/observability/audit");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement entry = doc.RootElement.EnumerateArray().First();

        entry.GetProperty("userId").GetString().Should().Be("user-42");
        entry.GetProperty("agentId").GetString().Should().Be("demand-agent");
        entry.GetProperty("tokens").GetInt32().Should().Be(200);
        // durationMs must be a JSON number (milliseconds), not a TimeSpan string.
        entry.GetProperty("durationMs").GetDouble().Should().BeApproximately(1234, 0.5);
    }

    private sealed class StubAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "tester")], "Test");
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), "Test");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}

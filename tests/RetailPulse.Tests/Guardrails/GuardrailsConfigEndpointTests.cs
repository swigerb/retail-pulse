using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
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
using RetailPulse.Api.Endpoints;
using RetailPulse.Api.Guardrails;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Tests.Guardrails;

public sealed class GuardrailsConfigEndpointTests
{
    [Fact]
    public async Task PutConfig_WithBackendContract_ThenGetReturnsChangedValue()
    {
        await using Host host = await Host.CreateAsync();

        HttpResponseMessage put = await host.Client.PutAsJsonAsync("/api/guardrails/config", new
        {
            jailbreakDetectionEnabled = false
        });

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        using var putBody = JsonDocument.Parse(await put.Content.ReadAsStringAsync());
        putBody.RootElement.GetProperty("jailbreakDetectionEnabled").GetBoolean().Should().BeFalse();
        putBody.RootElement.GetProperty("status").GetString().Should().Be("updated");

        HttpResponseMessage get = await host.Client.GetAsync("/api/guardrails/config");

        get.StatusCode.Should().Be(HttpStatusCode.OK);
        using var getBody = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        getBody.RootElement.GetProperty("jailbreakDetectionEnabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task PutConfig_WithLegacyClientNames_ReturnsBadRequestAndDoesNotApply()
    {
        await using Host host = await Host.CreateAsync();

        HttpResponseMessage put = await host.Client.PutAsJsonAsync("/api/guardrails/config", new
        {
            jailbreakEnabled = false,
            piiEnabled = false
        });

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        HttpResponseMessage get = await host.Client.GetAsync("/api/guardrails/config");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        using var getBody = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        getBody.RootElement.GetProperty("jailbreakDetectionEnabled").GetBoolean().Should().BeTrue();
        getBody.RootElement.GetProperty("piiDetectionEnabled").GetBoolean().Should().BeTrue();
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private Host(WebApplication app)
        {
            _app = app;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public static async Task<Host> CreateAsync()
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            builder.Services.AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, StubAuthHandler>("Test", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddRateLimiter(o =>
            {
                o.AddPolicy("relaxed", _ => RateLimitPartition.GetNoLimiter("all"));
                o.AddPolicy("moderate", _ => RateLimitPartition.GetNoLimiter("all"));
            });
            builder.Services.AddSingleton(new GuardrailsConfig
            {
                PiiDetectionEnabled = true,
                JailbreakDetectionEnabled = true,
                AutoRedactPii = true,
                MaxInputLength = 10000
            });
            builder.Services.AddSingleton<ISuspiciousRequestLog, InMemorySuspiciousRequestLog>();

            WebApplication app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseRateLimiter();
            app.MapGuardrailEndpoints();
            await app.StartAsync();
            return new Host(app);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class StubAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "test-user")],
                "Test");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}

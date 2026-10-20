using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Endpoints;
using RetailPulse.Api.Security;

namespace RetailPulse.Tests.Mcp;

/// <summary>
/// ADR 017 / issue #335: app-only route allow-list, API-side caps, stockout clamp and the
/// forecast seam, exercised through the real endpoint registrations on a TestServer.
/// </summary>
public sealed class McpGatewayApiTests
{
    private const string ShortScope = "scp";
    private const string LongScope = "http://schemas.microsoft.com/identity/claims/scope";

    private static readonly string[] V1Operations =
    [
        "GET /api/supply/inventory",
        "GET /api/stores/stockout-risks",
        "GET /api/supply/disruptions",
        "GET /api/demand/forecast",
    ];

    private sealed class StubHandler(Func<HttpRequestMessage, string> body, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body(request), Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string kind = Request.Headers["X-Caller"].ToString();
            if (kind == "none")
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>();
            switch (kind)
            {
                case "app": claims.Add(new Claim("roles", "RetailPulse.User")); break;
                case "short": claims.Add(new Claim(ShortScope, "access_as_user")); claims.Add(new Claim("roles", "RetailPulse.User")); break;
                case "long": claims.Add(new Claim(LongScope, "access_as_user")); claims.Add(new Claim("roles", "RetailPulse.User")); break;
                default: claims.Add(new Claim(ShortScope, "access_as_user")); break;
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(
        StubHandler handler, Dictionary<string, string?> config)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(config);
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(o => o.AddFixedWindowLimiter("relaxed", l =>
        {
            l.PermitLimit = 10000;
            l.Window = TimeSpan.FromMinutes(1);
        }));
        builder.Services.AddHttpClient("McpServer", c => c.BaseAddress = new Uri("http://mcp.internal"))
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<AppOnlyRouteAllowListMiddleware>();
        app.UseRateLimiter();
        app.MapSupplyEndpoints();
        app.MapStoreEndpoints();
        app.MapDemandEndpoints();
        app.MapMarginEndpoints();
        app.MapPost("/api/supply/inventory", () => Results.Ok()).RequireAuthorization();
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    private static Dictionary<string, string?> AllowList(params string[] ops)
    {
        var d = new Dictionary<string, string?>();
        for (int i = 0; i < ops.Length; i++) d[$"McpGateway:AllowedOperations:{i}"] = ops[i];
        return d;
    }

    private static HttpRequestMessage Req(HttpMethod method, string path, string caller)
    {
        var r = new HttpRequestMessage(method, path);
        r.Headers.Add("X-Caller", caller);
        return r;
    }

    [Theory]
    [InlineData("/api/supply/inventory")]
    [InlineData("/api/stores/stockout-risks")]
    [InlineData("/api/supply/disruptions")]
    [InlineData("/api/demand/forecast?brand=Acme")]
    public async Task AppOnly_AllowedGet_Succeeds(string path)
    {
        var handler = new StubHandler(_ => "{\"items\":[]}");
        (WebApplication app, HttpClient client) = await StartAsync(handler, AllowList(V1Operations));
        await using (app)
        {
            HttpResponseMessage res = await client.SendAsync(Req(HttpMethod.Get, path, "app"));
            res.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Theory]
    [InlineData("GET", "/api/margin/acme")]
    [InlineData("GET", "/api/supply/health?brand=a")]
    [InlineData("POST", "/api/supply/inventory")]
    public async Task AppOnly_NonAllowListedRouteOrMethod_Is403WithNoBody(string method, string path)
    {
        var handler = new StubHandler(_ => "{}");
        (WebApplication app, HttpClient client) = await StartAsync(handler, AllowList(V1Operations));
        await using (app)
        {
            HttpResponseMessage res = await client.SendAsync(Req(new HttpMethod(method), path, "app"));
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await res.Content.ReadAsStringAsync()).Should().BeEmpty();
            handler.Requests.Should().BeEmpty("a denied app-only call must never reach the MCP backend");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AppOnly_EmptyOrMissingAllowList_DeniesEverything(string? value)
    {
        var config = new Dictionary<string, string?>();
        if (value is not null) config["McpGateway:AllowedOperations:0"] = value;
        (WebApplication app, HttpClient client) = await StartAsync(new StubHandler(_ => "{}"), config);
        await using (app)
        {
            foreach (string op in V1Operations)
            {
                string path = op[4..] + (op.Contains("forecast") ? "?brand=a" : "");
                HttpResponseMessage res = await client.SendAsync(Req(HttpMethod.Get, path, "app"));
                res.StatusCode.Should().Be(HttpStatusCode.Forbidden, op);
            }
        }
    }

    [Fact]
    public async Task AppOnly_MethodMatchIsOrdinalAndTemplateCaseInsensitive()
    {
        (WebApplication app, HttpClient client) = await StartAsync(
            new StubHandler(_ => "{\"items\":[]}"), AllowList("get /api/supply/inventory"));
        await using (app)
        {
            (await client.SendAsync(Req(HttpMethod.Get, "/api/supply/inventory", "app"))).StatusCode
                .Should().Be(HttpStatusCode.Forbidden, "lower-case method is not an exact entry");
        }

        (app, client) = await StartAsync(new StubHandler(_ => "{\"items\":[]}"), AllowList("GET /API/Supply/Inventory"));
        await using (app)
        {
            (await client.SendAsync(Req(HttpMethod.Get, "/api/supply/inventory", "app"))).StatusCode
                .Should().Be(HttpStatusCode.OK);
        }
    }

    [Theory]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("delegated")]
    public async Task ScopeClaimInEitherForm_IsDelegated_AndBypassesAllowList(string caller)
    {
        (WebApplication app, HttpClient client) = await StartAsync(new StubHandler(_ => "{}"), AllowList());
        await using (app)
        {
            HttpResponseMessage res = await client.SendAsync(Req(HttpMethod.Get, "/api/margin/acme", caller));
            res.StatusCode.Should().Be(HttpStatusCode.OK, "tokens with scp/long scope claim plus roles are not app-only");
        }
    }

    [Fact]
    public async Task Unauthenticated_IsRejectedBeforeAllowList()
    {
        (WebApplication app, HttpClient client) = await StartAsync(new StubHandler(_ => "{}"), AllowList(V1Operations));
        await using (app)
        {
            HttpResponseMessage res = await client.SendAsync(Req(HttpMethod.Get, "/api/supply/inventory", "none"));
            res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(51, 50)]
    [InlineData(1000, 50)]
    [InlineData(7, 7)]
    public async Task StockoutRisks_LimitIsClampedToOneThroughFifty(int limit, int expected)
    {
        var handler = new StubHandler(_ => InventoryJson(200, status: "critical"));
        (WebApplication app, HttpClient client) = await StartAsync(handler, AllowList(V1Operations));
        await using (app)
        {
            HttpResponseMessage res = await client.SendAsync(Req(HttpMethod.Get, $"/api/stores/stockout-risks?limit={limit}", "app"));
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetArrayLength().Should().Be(expected);
        }
    }

    [Fact]
    public async Task StockoutRisks_DefaultLimitIsTwenty()
    {
        var handler = new StubHandler(_ => InventoryJson(200, status: "critical"));
        (WebApplication app, HttpClient client) = await StartAsync(handler, AllowList(V1Operations));
        await using (app)
        {
            HttpResponseMessage res = await client.SendAsync(Req(HttpMethod.Get, "/api/stores/stockout-risks", "app"));
            JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetArrayLength().Should().Be(20);
        }
    }

    [Fact]
    public async Task Inventory_AppOnly_IsCappedAtMaxItems()
    {
        var handler = new StubHandler(_ => InventoryJson(120, status: "ok"));
        (WebApplication app, HttpClient client) = await StartAsync(handler, AllowList(V1Operations));
        await using (app)
        {
            HttpResponseMessage res = await client.SendAsync(Req(HttpMethod.Get, "/api/supply/inventory", "app"));
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement
                .GetProperty("items").GetArrayLength().Should().Be(50);

            HttpResponseMessage spa = await client.SendAsync(Req(HttpMethod.Get, "/api/supply/inventory", "delegated"));
            JsonDocument.Parse(await spa.Content.ReadAsStringAsync()).RootElement
                .GetProperty("items").GetArrayLength().Should().Be(120, "delegated SPA callers are unaffected");
        }
    }

    [Fact]
    public async Task Inventory_AppOnly_HonoursConfiguredMaxItems()
    {
        Dictionary<string, string?> config = AllowList(V1Operations);
        config["McpGateway:MaxItems"] = "5";
        (WebApplication app, HttpClient client) = await StartAsync(new StubHandler(_ => InventoryJson(30, "ok")), config);
        await using (app)
        {
            HttpResponseMessage res = await client.SendAsync(Req(HttpMethod.Get, "/api/supply/inventory", "app"));
            JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement
                .GetProperty("items").GetArrayLength().Should().Be(5);
        }
    }

    [Fact]
    public async Task OversizePayload_AppOnly_Returns413()
    {
        Dictionary<string, string?> config = AllowList(V1Operations);
        config["McpGateway:MaxResponseBytes"] = "1024";
        var handler = new StubHandler(_ => "{\"items\":[\"" + new string('x', 5000) + "\"]}");
        (WebApplication app, HttpClient client) = await StartAsync(handler, config);
        await using (app)
        {
            HttpResponseMessage res = await client.SendAsync(Req(HttpMethod.Get, "/api/supply/disruptions", "app"));
            res.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
            (await res.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"response_too_large\"}");
        }
    }

    [Fact]
    public async Task Forecast_MissingBrand_Is400()
    {
        var handler = new StubHandler(_ => "{}");
        (WebApplication app, HttpClient client) = await StartAsync(handler, AllowList(V1Operations));
        await using (app)
        {
            HttpResponseMessage res = await client.SendAsync(Req(HttpMethod.Get, "/api/demand/forecast?brand=%20", "app"));
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            handler.Requests.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Forecast_CallsMcpForecastWithEscapedBrandAndDefaultRegion()
    {
        var handler = new StubHandler(_ => "{\"forecast\":[1,2]}");
        (WebApplication app, HttpClient client) = await StartAsync(handler, AllowList(V1Operations));
        await using (app)
        {
            HttpResponseMessage res = await client.SendAsync(Req(HttpMethod.Get, "/api/demand/forecast?brand=A%26B%20Co", "app"));
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            handler.Requests.Should().ContainSingle().Which.PathAndQuery
                .Should().Be("/api/forecast?brand=A%26B%20Co&region=National");
        }
    }

    [Fact]
    public async Task Forecast_BackendFailure_Is502()
    {
        var handler = new StubHandler(_ => "{}", HttpStatusCode.InternalServerError);
        (WebApplication app, HttpClient client) = await StartAsync(handler, AllowList(V1Operations));
        await using (app)
        {
            HttpResponseMessage res = await client.SendAsync(Req(HttpMethod.Get, "/api/demand/forecast?brand=Acme&region=West", "app"));
            res.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }
    }

    private static string InventoryJson(int count, string status)
    {
        var sb = new StringBuilder("{\"items\":[");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"{{\"sku\":\"S{i}\",\"brand\":\"B\",\"category\":\"C\",\"region\":\"R\",\"status\":\"{status}\",\"days_of_supply\":{i % 9 + 1},\"current_stock\":100,\"safety_stock\":50}}");
        }

        return sb.Append("]}").ToString();
    }
}

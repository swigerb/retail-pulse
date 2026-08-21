using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetailPulse.Api.Endpoints;
using RetailPulse.Api.Packs;

namespace RetailPulse.Tests.Packs;

/// <summary>
/// End-to-end smoke tests for <see cref="PackEndpoints"/>. Loads the
/// shipped default pack through <see cref="PackLoader"/>, wires it into
/// a minimal ASP.NET Core host, and verifies the JSON projections that
/// the frontend depends on.
/// </summary>
public sealed class PackEndpointsTests : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;

    public PackEndpointsTests()
    {
        LoadedPack pack = PackLoader.ForDirectory(PackTestPaths.PacksRoot).Load("default");

        _host = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddSingleton(pack);
                    services.AddRouting();
                    services.AddRateLimiter(options => options.AddPolicy("relaxed", _ =>
                            RateLimitPartition.GetFixedWindowLimiter("relaxed", _ =>
                                new FixedWindowRateLimiterOptions
                                {
                                    PermitLimit = 100,
                                    Window = TimeSpan.FromMinutes(1),
                                    QueueLimit = 0,
                                })));
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints => endpoints.MapPackEndpoints());
                });
            })
            .Build();

        _host.StartAsync().GetAwaiter().GetResult();
        _client = _host.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task GetPack_ReturnsMetadataAndTenantProjection()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/pack");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;

        root.GetProperty("key").GetString().Should().Be("default");
        root.GetProperty("displayName").GetString().Should().NotBeNullOrWhiteSpace();
        JsonElement tenant = root.GetProperty("tenant");
        tenant.GetProperty("company").GetString().Should().Be("Apex Retail Group");
        tenant.GetProperty("theme").GetProperty("primaryColor").GetString().Should().Be("#1B4D7A");
        tenant.GetProperty("brands").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetStartingTasks_ReturnsPackCategories()
    {
        PackStartingTasksResponse? payload =
            await _client.GetFromJsonAsync<PackStartingTasksResponse>("/api/pack/starting-tasks");

        payload.Should().NotBeNull();
        payload.PackKey.Should().Be("default");
        payload.Categories.Should().NotBeEmpty();
        payload.Categories.Should().AllSatisfy(c =>
        {
            c.Id.Should().NotBeNullOrWhiteSpace();
            c.Label.Should().NotBeNullOrWhiteSpace();
            c.Prompts.Should().NotBeEmpty();
        });
    }
}

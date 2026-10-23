using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RetailPulse.Api.Endpoints;
using RetailPulse.Api.Security.Anonymous;
using RetailPulse.Api.Security.GitHub;

namespace RetailPulse.Tests.Packs;

[Collection("ContentSafetyStartup")]
public sealed class PackHostStartupTests
{
    public static IEnumerable<object[]> PackAuthMatrix()
    {
        foreach (string pack in new[] { "default", "kroger-retail", "walgreens-retail", "staples-retail", "lowes-retail" })
        {
            foreach (string auth in new[] { "Entra", "Anonymous", "GitHub", "Development" })
            {
                yield return [pack, auth];
            }
        }
    }

    [Theory]
    [MemberData(nameof(PackAuthMatrix))]
    public async Task ProductionEntrypoint_MaterializesPackEndpoints_WithoutExternalCalls(string pack, string auth)
    {
        int outboundCalls = 0;
        WebApplicationBuilder trapBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        trapBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using WebApplication trap = trapBuilder.Build();
        trap.Run(context =>
        {
            Interlocked.Increment(ref outboundCalls);
            context.Response.StatusCode = 503;
            return Task.CompletedTask;
        });
        await trap.StartAsync();
        string trapUrl = trap.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        await using ApiProcess host = await ApiProcess.StartAsync(pack, auth, trapUrl);
        using var client = new HttpClient { BaseAddress = host.Address };

        using HttpResponseMessage alive = await client.GetAsync("/alive");
        alive.StatusCode.Should().Be(HttpStatusCode.OK);

        if (auth != "Development")
        {
            using HttpResponseMessage unauthenticated = await client.GetAsync("/api/info");
            unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            using HttpResponseMessage unauthenticatedCouncil = await client.PostAsJsonAsync(
                "/api/council/convene", new { brand = "Test brand" });
            unauthenticatedCouncil.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using HttpResponseMessage packResponse = await client.GetAsync("/api/pack");
        packResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var packJson = JsonDocument.Parse(await packResponse.Content.ReadAsStringAsync());
        packJson.RootElement.GetProperty("key").GetString().Should().Be(pack);

        if (auth == "Entra")
        {
            // No JWT/OIDC discovery or token acquisition is needed to prove the
            // production Entra endpoint graph starts and still rejects missing credentials.
            outboundCalls.Should().Be(0);
            return;
        }

        if (auth == "GitHub")
        {
            var options = new GitHubAuthOptions { SigningKey = host.SigningKey };
            var key = new GitHubSigningKeyProvider(options);
            var tokens = new GitHubSessionTokenService(options, key);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", tokens.CreateSession(new GitHubVerifiedUser(12345, "startup-test")).Token);
        }
        else if (auth == "Anonymous")
        {
            using HttpResponseMessage bootstrap = await client.PostAsync(AnonymousCapabilityPolicy.BootstrapRoute, null);
            bootstrap.StatusCode.Should().Be(HttpStatusCode.OK);
            using var session = JsonDocument.Parse(await bootstrap.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", session.RootElement.GetProperty("token").GetString());
        }

        using HttpResponseMessage history = await client.GetAsync("/api/council/history");
        history.StatusCode.Should().Be(auth == "Anonymous" ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
        if (auth != "Anonymous")
        {
            (await history.Content.ReadAsStringAsync()).Should().Be("[]");
        }

        if (pack != "default")
        {
            foreach ((string route, object body) in new (string, object)[]
            {
                ("/api/council/convene", new { brand = "Test brand" }),
                ("/api/scorecard", new { brands = new[] { "Test brand" } }),
                ("/api/escalate", new { message = "Test request" }),
            })
            {
                using HttpResponseMessage response = await client.PostAsJsonAsync(route, body);
                response.StatusCode.Should().Be(auth == "Anonymous"
                    ? HttpStatusCode.Forbidden
                    : HttpStatusCode.ServiceUnavailable, route);
            }
        }
        else
        {
            foreach ((string route, object body) in new (string, object)[]
            {
                ("/api/council/convene", new { brand = "" }),
                ("/api/scorecard", new { brands = Array.Empty<string>() }),
                ("/api/escalate", new { message = "" }),
            })
            {
                using HttpResponseMessage response = await client.PostAsJsonAsync(route, body);
                response.StatusCode.Should().Be(auth == "Anonymous"
                    ? HttpStatusCode.Forbidden
                    : HttpStatusCode.BadRequest, "the default pack's configured services must still resolve at " + route);
            }
        }

        // Validation exercises real chat/stream dependency binding but stops
        // before routing, planning, or any model/tool invocation.
        foreach (string route in new[] { "/api/chat", "/api/chat/stream" })
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(route, new { message = "" });
            bool forbidden = auth == "Anonymous" && route == "/api/chat/stream";
            response.StatusCode.Should().Be(forbidden ? HttpStatusCode.Forbidden : HttpStatusCode.BadRequest, route);
            if (!forbidden)
                (await response.Content.ReadAsStringAsync()).Should().Contain("message");
        }

        outboundCalls.Should().Be(0, "startup and these requests must not call models, MCP, or identity providers");
    }

    private sealed class ApiProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly string _directory;
        private readonly ConcurrentQueue<string> _output = new();
        private readonly TaskCompletionSource<Uri> _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _started;

        private ApiProcess(string directory, string signingKey, ProcessStartInfo start)
        {
            _directory = directory;
            SigningKey = signingKey;
            _process = new Process { StartInfo = start };
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not { } line)
                    return;
                _output.Enqueue(line);
                const string marker = "Now listening on: ";
                int index = line.IndexOf(marker, StringComparison.Ordinal);
                if (index >= 0)
                    _listening.TrySetResult(new Uri(line[(index + marker.Length)..].Trim()));
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { } line)
                    _output.Enqueue(line);
            };
        }

        public Uri Address { get; private set; } = null!;
        public string SigningKey { get; }

        public static async Task<ApiProcess> StartAsync(string pack, string auth, string trapUrl)
        {
            string directory = PackTestPaths.CreateFixtureDirectory("host");
            File.Copy(Path.Combine(PackTestPaths.RepoRoot, "src", "RetailPulse.Api", "appsettings.json"),
                Path.Combine(directory, "appsettings.json"));
            string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // Do not inherit live endpoints, credentials, exporters, or user secrets.
            start.Environment.Clear();
            start.Environment["HOME"] = directory;
            start.Environment["LOCALAPPDATA"] = directory;
            start.Environment["USERPROFILE"] = directory;
            foreach (string name in new[] { "PATH", "DOTNET_ROOT", "SystemRoot" })
            {
                if (Environment.GetEnvironmentVariable(name) is { } value)
                    start.Environment[name] = value;
            }
            start.ArgumentList.Add(typeof(ChatEndpoints).Assembly.Location);
            foreach ((string name, string value) in new Dictionary<string, string>
            {
                ["environment"] = auth == "Development" ? "Development" : "Production",
                ["urls"] = "http://127.0.0.1:0",
                ["contentRoot"] = directory,
                ["Packs:Root"] = PackTestPaths.PacksRoot,
                ["Packs:Active"] = pack,
                ["Authentication:Mode"] = auth == "Development" ? "Entra" : auth,
                ["MicrosoftEntra:TenantId"] = "11111111-1111-1111-1111-111111111111",
                ["MicrosoftEntra:ClientId"] = "33333333-3333-3333-3333-333333333333",
                ["MicrosoftEntra:Instance"] = trapUrl.Replace("http:", "https:", StringComparison.Ordinal),
                ["McpServer:BaseUrl"] = trapUrl,
                ["McpServer:ApiKey"] = key,
                ["OpenAI:Endpoint"] = trapUrl + "/inference",
                ["OpenAI:ApiKey"] = key,
                ["OpenAI:Deployment"] = "startup-test",
                ["RETAIL_PULSE_DATA_DIRECTORY"] = Path.Combine(directory, "data"),
                ["Anonymous:AllowHosted"] = "true",
                ["Anonymous:SigningKey"] = key,
                ["GitHub:ClientId"] = "startup-test",
                ["GitHub:ClientSecret"] = key,
                ["GitHub:SigningKey"] = key,
                ["GitHub:AllowedUserIds:0"] = "12345",
                ["GitHub:CallbackUrl"] = "https://api.example.test/api/auth/github/callback",
                ["GitHub:FrontendReturnUrl"] = "https://app.example.test/",
                ["GitHub:AcknowledgeSingleReplica"] = "true",
            })
            {
                start.ArgumentList.Add($"--{name}={value}");
            }

            var host = new ApiProcess(directory, key, start);
            try
            {
                host._started = host._process.Start();
                host._process.BeginOutputReadLine();
                host._process.BeginErrorReadLine();
                Task exit = host._process.WaitForExitAsync();
                Task completed = await Task.WhenAny(host._listening.Task, exit, Task.Delay(TimeSpan.FromSeconds(30)));
                if (completed != host._listening.Task)
                    throw new InvalidOperationException($"API did not start for {pack}/{auth}:\n{string.Join('\n', host._output)}");
                host.Address = await host._listening.Task;
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_started)
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            _process.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}

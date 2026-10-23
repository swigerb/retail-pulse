using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetailPulse.Api.Endpoints;
using RetailPulse.Api.Packs;
using RetailPulse.Api.Prompts;
using RetailPulse.Api.Security.GitHub;
using RetailPulse.Tests.Packs;
using Xunit.Abstractions;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

public sealed class ContentSafetyStartupHostTests(ITestOutputHelper testOutput)
{
    [LinuxOpenSslTheory]
    [Trait("Category", "LinuxOpenSslIntegration")]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public async Task ProductionEntrypoint_ScansRealRoster_AndPreservesUnavailablePolicy(
        bool regulated, bool disconnect, bool persistent)
    {
        string directory = PackTestPaths.CreateFixtureDirectory("startup-safety");
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest(
            "CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        certificateRequest.CertificateExtensions.Add(names.Build());
        certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using X509Certificate2 certificate = certificateRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        string trustPath = Path.Combine(directory, "loopback-ca.pem");
        await File.WriteAllTextAsync(trustPath, certificate.ExportCertificatePem());

        var shields = new ConcurrentQueue<string>();
        var analyses = new ConcurrentQueue<string>();
        List<(string Agent, string Field, string Text)> fields = [.. ExpectedFields()];
        string generalPrompt = fields.Single(f => f.Agent == "general" && f.Field == "SystemPrompt").Text;
        string generalFirstSegment = Api.Guardrails.ContentSafety.AzureContentSafetyEvaluator.SegmentForAnalysis(generalPrompt).First();
        fields.FindIndex(f => f.Agent == "general" && f.Field == "SystemPrompt").Should().BeGreaterThan(0,
            "general/SystemPrompt is not the first scan in the shipped default roster");
        int roots = 0;
        int identities = 0;
        int unexpected = 0;
        int missingBearer = 0;
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using WebApplication dependency = builder.Build();
        dependency.Run(async context =>
        {
            string path = context.Request.Path.Value!;
            if (path == "/identity")
            {
                Interlocked.Increment(ref identities);
                await context.Response.WriteAsJsonAsync(new
                {
                    access_token = "synthetic-startup-token",
                    expires_on = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                    token_type = "Bearer",
                    resource = "https://cognitiveservices.azure.com",
                });
                return;
            }

            if (path == "/" && context.Request.Method == "GET")
            {
                Interlocked.Increment(ref roots);
                context.Response.StatusCode = 404;
                return;
            }
            if (context.Request.Headers.Authorization != "Bearer synthetic-startup-token")
                Interlocked.Increment(ref missingBearer);
            if (path == "/contentsafety/text:shieldPrompt" && context.Request.Method == "POST")
            {
                using JsonDocument body = await JsonDocument.ParseAsync(context.Request.Body);
                string text = body.RootElement.GetProperty("userPrompt").GetString()!;
                shields.Enqueue(Hash(text));
                if (text.Length > 10_000)
                {
                    context.Response.StatusCode = 400;
                    return;
                }
                if (disconnect && (persistent || text == generalFirstSegment))
                {
                    context.Abort();
                    return;
                }
                await context.Response.WriteAsJsonAsync(new
                {
                    userPromptAnalysis = new { attackDetected = false },
                    documentsAnalysis = Array.Empty<object>(),
                });
                return;
            }
            if (path == "/contentsafety/text:analyze" && context.Request.Method == "POST")
            {
                using JsonDocument body = await JsonDocument.ParseAsync(context.Request.Body);
                analyses.Enqueue(Hash(body.RootElement.GetProperty("text").GetString()!));
                await context.Response.WriteAsJsonAsync(new { categoriesAnalysis = Array.Empty<object>() });
                return;
            }
            Interlocked.Increment(ref unexpected);
            context.Response.StatusCode = 503;
        });

        Process? process = null;
        var output = new ConcurrentQueue<string>();
        var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await dependency.StartAsync();
            string endpoint = dependency.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            File.Copy(Path.Combine(PackTestPaths.RepoRoot, "src", "RetailPulse.Api", "appsettings.json"),
                Path.Combine(directory, "appsettings.json"));
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // The child must never inherit real credentials, endpoints, or exporters.
            start.Environment.Clear();
            foreach (string name in new[] { "PATH", "DOTNET_ROOT", "SystemRoot" })
            {
                if (Environment.GetEnvironmentVariable(name) is { } value)
                    start.Environment[name] = value;
            }
            start.Environment["HOME"] = directory;
            start.Environment["LOCALAPPDATA"] = directory;
            start.Environment["USERPROFILE"] = directory;
            start.Environment["SSL_CERT_FILE"] = trustPath;
            start.Environment["IDENTITY_ENDPOINT"] = endpoint + "/identity";
            start.Environment["IDENTITY_HEADER"] = "synthetic-identity-header";
            string signingKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            start.ArgumentList.Add(typeof(ChatEndpoints).Assembly.Location);
            foreach ((string name, string value) in new Dictionary<string, string>
            {
                ["environment"] = "Production",
                ["urls"] = "http://127.0.0.1:0",
                ["contentRoot"] = directory,
                ["Packs:Root"] = PackTestPaths.PacksRoot,
                ["Packs:Active"] = "default",
                ["Authentication:Mode"] = "GitHub",
                ["GitHub:ClientId"] = "startup-test",
                ["GitHub:ClientSecret"] = signingKey,
                ["GitHub:SigningKey"] = signingKey,
                ["GitHub:AllowedUserIds:0"] = "12345",
                ["GitHub:CallbackUrl"] = "https://api.example.test/api/auth/github/callback",
                ["GitHub:FrontendReturnUrl"] = "https://app.example.test/",
                ["GitHub:AcknowledgeSingleReplica"] = "true",
                ["McpServer:BaseUrl"] = endpoint,
                ["McpServer:ApiKey"] = signingKey,
                ["OpenAI:Endpoint"] = endpoint + "/inference",
                ["OpenAI:ApiKey"] = "synthetic-model-key",
                ["OpenAI:Deployment"] = "startup-test",
                ["RETAIL_PULSE_DATA_DIRECTORY"] = Path.Combine(directory, "data"),
                ["Gateway:RegulatedRetail:Enabled"] = regulated.ToString(),
                ["Guardrails:ContentSafety:Enabled"] = "true",
                ["Guardrails:ContentSafety:Endpoint"] = endpoint,
                ["Guardrails:ContentSafety:OnUnavailable"] = "FailOpen",
                ["Guardrails:ContentSafety:TimeoutMs"] = "1500",
                ["Guardrails:ContentSafety:TokenTimeoutMs"] = "5000",
                ["Guardrails:ContentSafety:WarmUpTimeoutMs"] = "5000",
            })
            {
                start.ArgumentList.Add($"--{name}={value}");
            }
            process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not { } line)
                    return;
                output.Enqueue(line);
                const string marker = "Now listening on: ";
                int index = line.IndexOf(marker, StringComparison.Ordinal);
                if (index >= 0)
                    listening.TrySetResult(new Uri(line[(index + marker.Length)..].Trim()));
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { } line)
                    output.Enqueue(line);
            };
            process.Start().Should().BeTrue();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Task exit = process.WaitForExitAsync();
            Task completed = await Task.WhenAny(listening.Task, exit, Task.Delay(TimeSpan.FromSeconds(45)));
            bool refused = regulated && disconnect;
            if (refused)
            {
                completed.Should().Be(exit, string.Join('\n', output));
                await exit;
                process.ExitCode.Should().NotBe(0);
                listening.Task.IsCompleted.Should().BeFalse("fail-closed must refuse before accepting traffic");
            }
            else
            {
                completed.Should().Be(listening.Task, string.Join('\n', output));
                using var http = new HttpClient { BaseAddress = await listening.Task };
                using HttpResponseMessage alive = await http.GetAsync("/alive");
                alive.StatusCode.Should().Be(HttpStatusCode.OK);
                using HttpResponseMessage unauthenticated = await http.GetAsync("/api/guardrails/log");
                unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
                var authOptions = new GitHubAuthOptions { SigningKey = signingKey };
                var tokens = new GitHubSessionTokenService(authOptions, new GitHubSigningKeyProvider(authOptions));
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Bearer", tokens.CreateSession(new GitHubVerifiedUser(12345, "startup-test")).Token);
                using HttpResponseMessage audit = await http.GetAsync("/api/guardrails/log");
                audit.StatusCode.Should().Be(HttpStatusCode.OK);
                string rows = await audit.Content.ReadAsStringAsync();
                using var auditRows = JsonDocument.Parse(rows);
                auditRows.RootElement.GetArrayLength().Should().Be(disconnect ? 1 : 0);
                if (disconnect)
                {
                    rows.Should().Contain("agent-definition-content-safety-unavailable")
                        .And.Contain("failopen-passed")
                        .And.Contain("connection failed before a response")
                        .And.Contain("SystemPrompt on agent general");
                }
                else
                {
                    rows.Should().Be("[]");
                }
            }

            string logs = string.Join('\n', output);
            logs.Should().Contain("scanned 16 definition(s)")
                .And.Contain("71 content-safety call(s)")
                .And.Contain("Content Safety token warm-up completed")
                .And.Contain("Content Safety transport warm-up established a connection");
            logs.IndexOf("Content Safety transport warm-up established", StringComparison.Ordinal)
                .Should().BeLessThan(logs.IndexOf("AgentDefinitionValidator scanned", StringComparison.Ordinal),
                    "runtime hosted-service logs must not mask a silent startup mini-provider");
            if (disconnect)
            {
                logs.Should().Contain("Operation=PromptShield")
                    .And.Contain("FailureReason=Transport");
                if (refused)
                {
                    logs.Should().Contain("agent='general' field='SystemPrompt'")
                        .And.Contain("safety.content-safety-unavailable")
                        .And.Contain("connection failed before a response");
                }

                if (!persistent)
                    logs.Should().Contain($"with {(regulated ? 1 : 0)} violation(s)");
            }
            roots.Should().BeGreaterThanOrEqualTo(1);
            identities.Should().BeGreaterThanOrEqualTo(1);
            unexpected.Should().Be(0, "startup must not call models, MCP, or other dependencies");
            missingBearer.Should().Be(0);
            if (!persistent)
            {
                shields.Should().HaveCount(disconnect ? 71 : 72, "every required shield window is checked once; failures are not retried");
                shields.Should().BeEquivalentTo(fields.SelectMany(f =>
                {
                    IEnumerable<string> segments = Api.Guardrails.ContentSafety.AzureContentSafetyEvaluator.SegmentForAnalysis(f.Text);
                    return disconnect && f.Agent == "general" && f.Field == "SystemPrompt" ? segments.Take(1) : segments;
                }).Select(Hash), "every field must reach the evaluator; only a failed field may stop before its next segment");
                shields.ToList().IndexOf(Hash(generalFirstSegment)).Should().Be(28,
                    "the observed general/SystemPrompt request is field 29, not a cold first POST");
                analyses.Should().BeEquivalentTo(fields
                    .Where(f => !disconnect || f.Agent != "general" || f.Field != "SystemPrompt")
                    .SelectMany(f => Api.Guardrails.ContentSafety.AzureContentSafetyEvaluator.SegmentForAnalysis(f.Text))
                    .Select(Hash));
            }
            else
            {
                shields.Should().HaveCount(4,
                    "the root GET plus four failed POSTs reach the breaker's minimum throughput of five");
                analyses.Should().BeEmpty();
                logs.Should().Contain("FailureReason=CircuitOpen").And.Contain("with 71 violation(s)");
            }
            testOutput.WriteLine(
                "Production entrypoint: regulated={0}, disconnect={1}, persistent={2}, fields=71, " +
                "shield POSTs={3}, moderation POSTs={4}, general/SystemPrompt field ordinal={5}, unexpected calls={6}.",
                regulated, disconnect, persistent, shields.Count, analyses.Count,
                fields.FindIndex(f => f.Agent == "general" && f.Field == "SystemPrompt") + 1, unexpected);
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                process.Dispose();
            }
            await dependency.StopAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static IEnumerable<(string Agent, string Field, string Text)> ExpectedFields()
    {
        LoadedPack pack = PackLoader.ForDirectory(PackTestPaths.PacksRoot).Load("default");
        var engine = new PromptTemplateEngine(new PackTenantProvider(pack).GetTenant());
        foreach ((string section, Api.Models.AgentDefinition definition) in pack.Agents.Agents)
        {
            if (string.IsNullOrWhiteSpace(definition.Key))
                definition.Key = section;
            if (!string.Equals(definition.Key, "router", StringComparison.OrdinalIgnoreCase))
                engine.Hydrate(definition);
            foreach ((string field, string text) in new[]
                {
                    ("SystemPrompt", definition.SystemPrompt),
                    ("DisplayName", definition.DisplayName),
                    ("FallbackReply", definition.FallbackReply),
                }.Concat(definition.KeywordFastPaths.Select((text, index) => ($"KeywordFastPaths[{index}]", text))))
            {
                if (!string.IsNullOrWhiteSpace(text))
                    yield return (definition.Key, field, text);
            }
        }
    }
}

public sealed class LinuxOpenSslTheoryAttribute : TheoryAttribute
{
    public LinuxOpenSslTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Linux/OpenSSL integration test: the child process requires SSL_CERT_FILE PEM trust. " +
                "Windows Schannel and macOS native trust do not use this fixture's per-process trust file. " +
                "Run ContentSafetyStartupHostTests on Linux (.NET 10), as required by the Linux CI job.";
        }
    }
}

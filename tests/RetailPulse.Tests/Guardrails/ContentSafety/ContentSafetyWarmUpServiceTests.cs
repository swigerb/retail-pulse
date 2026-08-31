using System.Diagnostics;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// <see cref="ContentSafetyWarmUpService"/> primes the token and the HTTPS
/// connection before the first real scan, and must never gate host startup on
/// either. These tests pin all three halves: <see cref="ContentSafetyWarmUpService.StartAsync"/>
/// returns effectively immediately even when the credential never answers, the
/// background warm-up is time-boxed, and the connection to the shared client is
/// actually opened (issue #273, where a warm token still left the first scan
/// paying the TLS handshake and failing open).
/// </summary>
public class ContentSafetyWarmUpServiceTests
{
    [Fact]
    public async Task StartAsync_ReturnsImmediately_EvenWhenCredentialHangs()
    {
        var hangingCredential = new ProgrammableTokenCredential(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return default;
        });
        ContentSafetyWarmUpService service = Build(hangingCredential, warmUpTimeoutMs: 300, out _);

        var sw = Stopwatch.StartNew();
        await service.StartAsync(CancellationToken.None);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(200),
            "warm-up is fire-and-forget; host startup must not wait on the credential");

        // The background warm-up must still terminate within its own budget rather
        // than run forever.
        Task completion = service.WarmUpCompletion
            ?? throw new InvalidOperationException("StartAsync must set WarmUpCompletion.");
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        completion.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_WarmsToken_WhenCredentialSucceeds()
    {
        var credential = new ProgrammableTokenCredential((_, _) =>
            ValueTask.FromResult(ProgrammableTokenCredential.FreshToken("warm")));
        var provider = new ContentSafetyTokenProvider(credential);
        var factory = new StubHttpClientFactory();
        var service = new ContentSafetyWarmUpService(
            provider,
            factory,
            ConfigWith(warmUpTimeoutMs: 5000),
            NullLogger<ContentSafetyWarmUpService>.Instance);

        await service.StartAsync(CancellationToken.None);
        Task completion = service.WarmUpCompletion
            ?? throw new InvalidOperationException("StartAsync must set WarmUpCompletion.");
        await completion.WaitAsync(TimeSpan.FromSeconds(5));

        // The primed token must serve the first real scan without another fetch.
        string bearer = await provider.GetBearerAsync(CancellationToken.None);
        bearer.Should().Be("warm");
        credential.Calls.Should().Be(1, "warm-up primed the cache; the first scan must be a cache hit");
    }

    [Fact]
    public async Task WarmAsync_OpensAConnectionOnTheSharedClient()
    {
        ContentSafetyWarmUpService service = Build(SucceedingCredential(), warmUpTimeoutMs: 5000, out StubHttpClientFactory factory);

        await service.WarmAsync(CancellationToken.None);

        factory.Handler.CallCount.Should().Be(1,
            "one round trip is enough to complete DNS, TCP and TLS and leave a pooled connection");

        // Warming a different pool would prime nothing the scan can use, so the name
        // is the load-bearing part of this assertion.
        factory.RequestedNames.Should().ContainSingle()
            .Which.Should().Be(ContentSafetyServiceCollectionExtensions.HttpClientName);
    }

    [Fact]
    public async Task WarmAsync_RetriesTheHandshakeOnce_WhenTheFirstAttemptFails()
    {
        ContentSafetyWarmUpService service = Build(SucceedingCredential(), warmUpTimeoutMs: 5000, out StubHttpClientFactory factory);
        factory.Handler.Responder = (_, _) => factory.Handler.CallCount == 1
            ? throw new HttpRequestException("connection refused")
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        await service.WarmAsync(CancellationToken.None);

        factory.Handler.CallCount.Should().Be(2,
            "the per-attempt timeout is fixed, so a second attempt is the only way spare budget helps");
    }

    [Fact]
    public async Task WarmAsync_DoesNotThrow_WhenEveryHandshakeAttemptFails()
    {
        ContentSafetyWarmUpService service = Build(SucceedingCredential(), warmUpTimeoutMs: 5000, out StubHttpClientFactory factory);
        factory.Handler.Responder = (_, _) => throw new HttpRequestException("endpoint unreachable");

        // Fail-open policy is unchanged by warm-up: an unreachable endpoint at start
        // must degrade to a cold first scan, never to a failed host start.
        Func<Task> act = () => service.WarmAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        factory.Handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task WarmAsync_SkipsTheHandshake_WhenTheTokenConsumedTheWholeBudget()
    {
        var hangingCredential = new ProgrammableTokenCredential(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return default;
        });
        ContentSafetyWarmUpService service = Build(hangingCredential, warmUpTimeoutMs: 300, out StubHttpClientFactory factory);

        await service.WarmAsync(CancellationToken.None);

        // One deadline covers the whole warm-up, and a handshake needs a viable
        // slice of it. Starting one on the remnant of a budget the token already
        // spent would cancel mid-flight and leave nothing pooled for the first scan.
        factory.Handler.CallCount.Should().Be(0);
    }

    private static ProgrammableTokenCredential SucceedingCredential() =>
        new((_, _) => ValueTask.FromResult(ProgrammableTokenCredential.FreshToken("warm")));

    private static ContentSafetyWarmUpService Build(
        ProgrammableTokenCredential credential,
        int warmUpTimeoutMs,
        out StubHttpClientFactory factory)
    {
        factory = new StubHttpClientFactory();
        return new ContentSafetyWarmUpService(
            new ContentSafetyTokenProvider(credential),
            factory,
            ConfigWith(warmUpTimeoutMs),
            NullLogger<ContentSafetyWarmUpService>.Instance);
    }

    private static GuardrailsConfig ConfigWith(int warmUpTimeoutMs) => new()
    {
        ContentSafety = new ContentSafetyConfig
        {
            Enabled = true,
            PromptShieldsEnabled = true,
            WarmUpTimeoutMs = warmUpTimeoutMs,
        },
    };

    /// <summary>
    /// Hands out one client backed by <see cref="CapturingHttpMessageHandler"/> and
    /// records the names asked for, so a test can prove the warm-up primed the
    /// shared Content Safety pool rather than an unrelated one.
    /// </summary>
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public CapturingHttpMessageHandler Handler { get; } = new();
        public List<string> RequestedNames { get; } = [];

        public HttpClient CreateClient(string name)
        {
            RequestedNames.Add(name);
            return new HttpClient(Handler, disposeHandler: false)
            {
                BaseAddress = new Uri("https://cs-test.cognitiveservices.azure.com/"),
            };
        }
    }
}

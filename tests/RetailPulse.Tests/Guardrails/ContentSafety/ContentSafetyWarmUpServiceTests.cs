using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// <see cref="ContentSafetyWarmUpService"/> primes the runtime token before the
/// first real scan, but it must never gate host startup on a remote credential
/// call. These tests pin both halves: <see cref="ContentSafetyWarmUpService.StartAsync"/>
/// returns effectively immediately even when the credential never answers, and
/// the background warm-up is itself time-boxed.
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
        ContentSafetyWarmUpService service = Build(hangingCredential, warmUpTimeoutMs: 300);

        var sw = Stopwatch.StartNew();
        await service.StartAsync(CancellationToken.None);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(200),
            "warm-up is fire-and-forget; host startup must not wait on the credential");

        // The background warm-up must still terminate within its own budget rather
        // than run forever.
        Task completion = service.WarmUpCompletion
            ?? throw new InvalidOperationException("StartAsync must set WarmUpCompletion.");
        await completion.WaitAsync(TimeSpan.FromSeconds(3));
        completion.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_WarmsToken_WhenCredentialSucceeds()
    {
        var credential = new ProgrammableTokenCredential((_, _) =>
            ValueTask.FromResult(ProgrammableTokenCredential.FreshToken("warm")));
        var provider = new ContentSafetyTokenProvider(credential);
        var service = new ContentSafetyWarmUpService(
            provider,
            ConfigWith(warmUpTimeoutMs: 5000),
            NullLogger<ContentSafetyWarmUpService>.Instance);

        await service.StartAsync(CancellationToken.None);
        Task completion = service.WarmUpCompletion
            ?? throw new InvalidOperationException("StartAsync must set WarmUpCompletion.");
        await completion.WaitAsync(TimeSpan.FromSeconds(3));

        // The primed token must serve the first real scan without another fetch.
        string bearer = await provider.GetBearerAsync(CancellationToken.None);
        bearer.Should().Be("warm");
        credential.Calls.Should().Be(1, "warm-up primed the cache; the first scan must be a cache hit");
    }

    private static ContentSafetyWarmUpService Build(
        ProgrammableTokenCredential credential, int warmUpTimeoutMs)
    {
        return new ContentSafetyWarmUpService(
            new ContentSafetyTokenProvider(credential),
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
}

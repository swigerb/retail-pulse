using System.Diagnostics;
using Azure.Core;
using Azure.Identity;
using FluentAssertions;
using RetailPulse.Api.Guardrails.ContentSafety;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// Cold-start warm-up invariants for <see cref="ContentSafetyTokenProvider"/>.
///
/// The four fail-open audit rows measured immediately after service start came
/// from the agent-definition scan racing an unprimed managed-identity token
/// fetch inside the per-scan timeout. Warm-up moves that first fetch off the
/// scan critical path. These tests pin its contract:
///
/// 1. A genuinely transient credential failure is retried and the warm-up
///    succeeds.
/// 2. An authentication rejection is NOT retried. Retrying a bad identity is
///    pointless and wasteful.
/// 3. The warm-up is time-boxed: a credential that never answers cannot stall
///    startup.
/// </summary>
public class ContentSafetyTokenProviderWarmUpTests
{
    [Fact]
    public async Task WarmUpAsync_TransientFirstFailure_IsRetried_AndPrimesCache()
    {
        var credential = new ProgrammableTokenCredential((attempt, _) => attempt == 1
            ? throw new CredentialUnavailableException("IMDS endpoint still warming")
            : ValueTask.FromResult(ProgrammableTokenCredential.FreshToken("primed")));
        var provider = new ContentSafetyTokenProvider(credential);

        ContentSafetyWarmUpResult result = await provider.WarmUpAsync(
            TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Should().Be(ContentSafetyWarmUpResult.Warmed);
        credential.Calls.Should().Be(2, "a transient credential failure must be retried");

        // The primed token must satisfy the fast path so the first real scan is a
        // cache hit and never pays the login round-trip again.
        string bearer = await provider.GetBearerAsync(CancellationToken.None);
        bearer.Should().Be("primed");
        credential.Calls.Should().Be(2, "warm-up must have primed the cache; no extra fetch on first use");
    }

    [Fact]
    public async Task WarmUpAsync_AuthenticationFailure_IsNotRetried()
    {
        var credential = new ProgrammableTokenCredential((_, _) =>
            throw new AuthenticationFailedException("managed identity is misconfigured"));
        var provider = new ContentSafetyTokenProvider(credential);

        ContentSafetyWarmUpResult result = await provider.WarmUpAsync(
            TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Should().Be(ContentSafetyWarmUpResult.AuthenticationFailed);
        credential.Calls.Should().Be(1,
            "an authentication rejection is terminal; retrying a bad identity cannot succeed");
    }

    [Fact]
    public async Task WarmUpAsync_CredentialNeverAnswers_IsTimeBoxed_AndDoesNotBlock()
    {
        var credential = new ProgrammableTokenCredential(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return default;
        });
        var provider = new ContentSafetyTokenProvider(credential);

        var sw = Stopwatch.StartNew();
        ContentSafetyWarmUpResult result = await provider.WarmUpAsync(
            TimeSpan.FromMilliseconds(300), CancellationToken.None);
        sw.Stop();

        result.Should().Be(ContentSafetyWarmUpResult.TimedOut);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3),
            "a hanging credential must be bounded by the warm-up budget, never block startup indefinitely");
    }

    [Fact]
    public async Task WarmUpAsync_WhenAlreadyCached_DoesNotCallCredential()
    {
        var credential = new ProgrammableTokenCredential((_, _) =>
            ValueTask.FromResult(ProgrammableTokenCredential.FreshToken("first")));
        var provider = new ContentSafetyTokenProvider(credential);

        _ = await provider.GetBearerAsync(CancellationToken.None);
        credential.Calls.Should().Be(1);

        ContentSafetyWarmUpResult result = await provider.WarmUpAsync(
            TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Should().Be(ContentSafetyWarmUpResult.AlreadyWarm);
        credential.Calls.Should().Be(1, "a valid cached token needs no warm-up fetch");
    }
}

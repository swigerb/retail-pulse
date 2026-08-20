using FluentAssertions;
using RetailPulse.Api.Guardrails.ContentSafety;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// Companion to the Prompt Shields authentication test: proves the shared
/// <see cref="ContentSafetyTokenProvider"/> caches tokens across concurrent
/// callers and returns a valid bearer on the fast path. If either invariant
/// breaks, Prompt Shields would silently pay a login round-trip on every
/// request.
/// </summary>
public class ContentSafetyTokenProviderTests
{
    [Fact]
    public async Task GetBearerAsync_ReturnsTokenFromCredential()
    {
        var credential = new FakeTokenCredential(token: "abc");
        var provider = new ContentSafetyTokenProvider(credential);

        string token = await provider.GetBearerAsync(CancellationToken.None);

        token.Should().Be("abc");
        credential.Calls.Should().Be(1);
    }

    [Fact]
    public async Task GetBearerAsync_ConcurrentCallers_TriggerSingleCredentialInvocation()
    {
        var credential = new FakeTokenCredential(token: "abc");
        var provider = new ContentSafetyTokenProvider(credential);

        Task<string>[] tasks = [.. Enumerable.Range(0, 32).Select(_ => provider.GetBearerAsync(CancellationToken.None).AsTask())];
        string[] results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(t => t == "abc");
        credential.Calls.Should().Be(1,
            "the SemaphoreSlim must collapse concurrent refreshes into a single credential call");
    }

    [Fact]
    public async Task GetBearerAsync_RefreshesWhenLifetimeShort()
    {
        var credential = new FakeTokenCredential(token: "abc", lifetime: TimeSpan.FromSeconds(30));
        var provider = new ContentSafetyTokenProvider(credential);

        _ = await provider.GetBearerAsync(CancellationToken.None);
        _ = await provider.GetBearerAsync(CancellationToken.None);

        credential.Calls.Should().Be(2,
            "a token that expires inside the refresh-buffer window must be re-fetched on the next call");
    }
}

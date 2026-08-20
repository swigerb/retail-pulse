using Azure.Core;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// Deterministic <see cref="TokenCredential"/> used by the Prompt Shields
/// authentication and token-cache tests. Increments a call counter every time
/// the credential is invoked so a test can assert caching behaviour without
/// relying on wall-clock timing.
/// </summary>
internal sealed class FakeTokenCredential : TokenCredential
{
    private readonly string _token;
    private readonly TimeSpan _lifetime;
    private int _calls;

    public FakeTokenCredential(string token = "test-bearer-token", TimeSpan? lifetime = null)
    {
        _token = token;
        _lifetime = lifetime ?? TimeSpan.FromHours(1);
    }

    public int Calls => Volatile.Read(ref _calls);

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return new AccessToken(_token, DateTimeOffset.UtcNow.Add(_lifetime));
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return ValueTask.FromResult(new AccessToken(_token, DateTimeOffset.UtcNow.Add(_lifetime)));
    }
}

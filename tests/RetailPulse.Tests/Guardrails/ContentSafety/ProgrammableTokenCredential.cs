using Azure.Core;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// A <see cref="TokenCredential"/> whose behaviour is scripted per call, used by
/// the cold-start warm-up tests. Unlike <see cref="FakeTokenCredential"/> (which
/// always answers instantly) this can throw, delay, or fail on the first call and
/// succeed on a later one, so the retry / no-retry / time-box invariants can be
/// asserted deterministically.
/// </summary>
internal sealed class ProgrammableTokenCredential : TokenCredential
{
    private readonly Func<int, CancellationToken, ValueTask<AccessToken>> _onCall;
    private int _calls;

    public ProgrammableTokenCredential(Func<int, CancellationToken, ValueTask<AccessToken>> onCall)
    {
        _onCall = onCall;
    }

    public int Calls => Volatile.Read(ref _calls);

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        int attempt = Interlocked.Increment(ref _calls);
        return _onCall(attempt, cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        int attempt = Interlocked.Increment(ref _calls);
        return await _onCall(attempt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A far-future token so <c>HasSufficientLifetime</c> treats it as fresh.</summary>
    public static AccessToken FreshToken(string token = "warmed-token") =>
        new(token, DateTimeOffset.UtcNow.AddHours(1));
}

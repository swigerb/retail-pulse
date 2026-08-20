using System.Threading;
using Azure.Core;

namespace RetailPulse.Api.Guardrails.ContentSafety;

/// <summary>
/// Async, thread-safe access-token cache used by the Prompt Shields raw HTTP
/// path so it authenticates through the same
/// <see cref="TokenCredential"/> singleton that the
/// <see cref="Azure.AI.ContentSafety.ContentSafetyClient"/> uses for text
/// moderation. Keeping a single credential means one managed-identity login
/// stream and one token cache — no per-call login path and no drift between
/// the two evaluator paths (which is critical because
/// <c>disableLocalAuth=true</c> is set on the provisioned Cognitive Services
/// account).
/// </summary>
/// <remarks>
/// The provider caches the last <see cref="AccessToken"/> and refreshes it
/// when the remaining lifetime drops under <see cref="RefreshBuffer"/>. A
/// single <see cref="SemaphoreSlim"/> serialises refresh so concurrent
/// evaluator calls issue at most one credential request per rotation.
/// </remarks>
public sealed class ContentSafetyTokenProvider
{
    /// <summary>Refresh a cached token when this much lifetime remains.</summary>
    public static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(5);

    /// <summary>The single OAuth scope required by the Content Safety data plane.</summary>
    public const string Scope = "https://cognitiveservices.azure.com/.default";

    private static readonly string[] _scopes = [Scope];

    private readonly TokenCredential _credential;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AccessToken _cached;

    public ContentSafetyTokenProvider(TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        _credential = credential;
    }

    /// <summary>Returns a valid bearer token, refreshing under the semaphore if necessary.</summary>
    public async ValueTask<string> GetBearerAsync(CancellationToken cancellationToken)
    {
        // Fast path — token still fresh.
        if (HasSufficientLifetime(_cached))
        {
            return _cached.Token;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (HasSufficientLifetime(_cached))
            {
                return _cached.Token;
            }
            _cached = await _credential.GetTokenAsync(
                new TokenRequestContext(_scopes),
                cancellationToken).ConfigureAwait(false);
            return _cached.Token;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static bool HasSufficientLifetime(AccessToken token) =>
        token.Token is { Length: > 0 } && token.ExpiresOn - RefreshBuffer > DateTimeOffset.UtcNow;
}

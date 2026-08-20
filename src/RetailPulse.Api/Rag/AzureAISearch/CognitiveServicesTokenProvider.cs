using Azure.Core;

namespace RetailPulse.Api.Rag.AzureAISearch;

/// <summary>
/// Async, thread-safe access-token cache used by the embeddings HTTP path so
/// it authenticates through the same <see cref="TokenCredential"/> singleton
/// that the Azure.Search SDK uses. Mirrors the ContentSafety token provider
/// pattern so a single managed-identity login stream serves every path.
/// </summary>
public sealed class CognitiveServicesTokenProvider
{
    /// <summary>Refresh a cached token when this much lifetime remains.</summary>
    public static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(5);

    /// <summary>OAuth scope for the Azure OpenAI data plane (chat + embeddings).</summary>
    public const string Scope = "https://cognitiveservices.azure.com/.default";

    private static readonly string[] _scopes = [Scope];

    private readonly TokenCredential _credential;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AccessToken _cached;

    public CognitiveServicesTokenProvider(TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        _credential = credential;
    }

    /// <summary>Returns a valid bearer token, refreshing under the semaphore if necessary.</summary>
    public async ValueTask<string> GetBearerAsync(CancellationToken cancellationToken)
    {
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

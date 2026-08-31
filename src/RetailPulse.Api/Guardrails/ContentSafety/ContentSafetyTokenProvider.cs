using System.Diagnostics;
using System.Threading;
using Azure.Core;
using Azure.Identity;

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

    /// <summary>
    /// Pre-acquires a token before any real scan runs so the first scan does not
    /// pay the unprimed AAD/IMDS round-trip inside its own timeout budget. This
    /// is the cold-start remedy: the expensive first login is moved here, off the
    /// per-scan critical path.
    /// </summary>
    /// <remarks>
    /// The whole call is bounded by <paramref name="budget"/> and never throws, so
    /// it cannot stall host startup. Only genuinely transient failures are retried
    /// with a short backoff; an authentication rejection is terminal and is NOT
    /// retried, because a bad identity configuration does not become valid by
    /// asking again.
    /// </remarks>
    public async Task<ContentSafetyWarmUpResult> WarmUpAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        if (HasSufficientLifetime(_cached))
        {
            return ContentSafetyWarmUpResult.AlreadyWarm;
        }

        TimeSpan boundedBudget = budget < _minWarmUpBudget ? _minWarmUpBudget : budget;
        long deadline = Stopwatch.GetTimestamp() + (long)(boundedBudget.TotalSeconds * Stopwatch.Frequency);

        for (int attempt = 1; attempt <= _maxWarmUpAttempts; attempt++)
        {
            TimeSpan remaining = RemainingBudget(deadline);
            if (remaining <= TimeSpan.Zero)
            {
                return ContentSafetyWarmUpResult.TimedOut;
            }

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(remaining);
            try
            {
                AccessToken token = await _credential.GetTokenAsync(
                    new TokenRequestContext(_scopes),
                    attemptCts.Token).ConfigureAwait(false);
                await StoreAsync(token, cancellationToken).ConfigureAwait(false);
                return ContentSafetyWarmUpResult.Warmed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ContentSafetyWarmUpResult.Cancelled;
            }
            catch (OperationCanceledException)
            {
                // The attempt exceeded the remaining budget. The whole warm-up is
                // time-boxed, so stop rather than burn the next attempt on a
                // credential endpoint that is not answering in time.
                return ContentSafetyWarmUpResult.TimedOut;
            }
            catch (CredentialUnavailableException)
            {
                // The credential source (for example the IMDS endpoint) is not
                // answering yet. That is exactly the cold-start condition warm-up
                // exists to ride out, so this is transient and retried.
                if (attempt == _maxWarmUpAttempts || !await BackoffAsync(deadline, cancellationToken).ConfigureAwait(false))
                {
                    return ContentSafetyWarmUpResult.TransientExhausted;
                }
            }
            catch (AuthenticationFailedException)
            {
                // The identity was reached and rejected. Retrying cannot fix a
                // misconfigured identity, so fail fast and let the operator see it.
                return ContentSafetyWarmUpResult.AuthenticationFailed;
            }
        }

        return ContentSafetyWarmUpResult.TransientExhausted;
    }

    private async Task StoreAsync(AccessToken token, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cached = token;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static TimeSpan RemainingBudget(long deadline)
    {
        long ticks = deadline - Stopwatch.GetTimestamp();
        return ticks <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
    }

    private static async Task<bool> BackoffAsync(long deadline, CancellationToken cancellationToken)
    {
        TimeSpan remaining = RemainingBudget(deadline);
        if (remaining <= _warmUpBackoff)
        {
            return false;
        }

        try
        {
            await Task.Delay(_warmUpBackoff, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private const int _maxWarmUpAttempts = 3;
    private static readonly TimeSpan _warmUpBackoff = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan _minWarmUpBudget = TimeSpan.FromMilliseconds(200);
}

/// <summary>Outcome of <see cref="ContentSafetyTokenProvider.WarmUpAsync"/>.</summary>
public enum ContentSafetyWarmUpResult
{
    /// <summary>A token was acquired and cached; the first scan will be warm.</summary>
    Warmed,

    /// <summary>A valid token was already cached; no acquisition was needed.</summary>
    AlreadyWarm,

    /// <summary>Authentication was rejected. Terminal, not retried.</summary>
    AuthenticationFailed,

    /// <summary>The time box elapsed before a token could be acquired.</summary>
    TimedOut,

    /// <summary>Every transient attempt failed within the budget.</summary>
    TransientExhausted,

    /// <summary>The caller cancelled the warm-up.</summary>
    Cancelled,
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace RetailPulse.Api.Security.GitHub;

/// <summary>
/// The server-side record bound to a single OAuth <c>state</c> value. The <c>state</c> string itself
/// is the dictionary key (never stored in this record); the record holds the HASH of the browser-bound
/// cookie secret and the expiry. Verification requires the caller to present BOTH the matching state
/// (query) and the matching cookie secret, defeating login-CSRF and state fixation.
/// </summary>
/// <param name="CookieSecretHash">SHA-256 of the random secret placed in the HttpOnly state cookie.</param>
/// <param name="ExpiresAtTicks">Absolute UTC expiry (ticks). Past this the entry is invalid.</param>
public readonly record struct GitHubStateEntry(byte[] CookieSecretHash, long ExpiresAtTicks);

/// <summary>
/// The server-side record redeemed by the SPA at the exchange endpoint. It holds the VERIFIED GitHub
/// identity (immutable numeric id + login) — NOT a provider token and NOT an app token. The app
/// session JWT is minted fresh at redemption so its short TTL starts when the SPA receives it.
/// </summary>
/// <param name="User">The verified GitHub identity established during the callback.</param>
/// <param name="ExpiresAtTicks">Absolute UTC expiry (ticks). Past this the code is invalid.</param>
public readonly record struct GitHubRedemptionEntry(GitHubVerifiedUser User, long ExpiresAtTicks);

/// <summary>
/// A bounded, thread-safe, in-memory store with atomic one-time consumption and TTL, used for both
/// the OAuth state entries and the post-login redemption codes.
///
/// Security properties:
/// <list type="bullet">
///   <item><b>One-time:</b> <see cref="TryConsume"/> removes the entry atomically
///     (<see cref="ConcurrentDictionary{TKey,TValue}.TryRemove(TKey, out TValue)"/>), so two racing
///     callers can never both succeed — this defeats state/code replay and exchange races.</item>
///   <item><b>TTL:</b> an entry past its expiry is treated as absent (and removed), bounding the
///     window for a captured value.</item>
///   <item><b>Bounded + cleanup:</b> the store caps its size and opportunistically sweeps expired
///     entries, so a flood of unfinished logins cannot exhaust memory.</item>
/// </list>
/// Keys are compared with <see cref="StringComparer.Ordinal"/>. The store is replica-local — see the
/// deployment note: hosted GitHub mode is pinned to a single replica unless moved to a distributed
/// store, because a callback handled by replica A cannot redeem a code on replica B.
/// </summary>
/// <typeparam name="TValue">The stored entry type (must expose its expiry via the selector).</typeparam>
public class OneTimeTtlStore<TValue>
{
    private readonly ConcurrentDictionary<string, TValue> _entries = new(StringComparer.Ordinal);
    private readonly Func<TValue, long> _expirySelector;
    private readonly TimeProvider _timeProvider;
    private readonly int _maxEntries;
    private long _lastSweepTicks;

    public OneTimeTtlStore(Func<TValue, long> expirySelector, int maxEntries = 10_000, TimeProvider? timeProvider = null)
    {
        _expirySelector = expirySelector ?? throw new ArgumentNullException(nameof(expirySelector));
        _maxEntries = maxEntries > 0 ? maxEntries : throw new ArgumentOutOfRangeException(nameof(maxEntries));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Current live entry count (used by tests to assert cleanup).</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Stores <paramref name="value"/> under <paramref name="key"/>. Returns false if the store is at
    /// capacity after a sweep (fail-closed rather than evicting an in-flight login). A duplicate key
    /// (astronomically unlikely for a 256-bit random key) is rejected.
    /// </summary>
    public bool TryStore(string key, TValue value)
    {
        long nowTicks = _timeProvider.GetUtcNow().UtcDateTime.Ticks;
        MaybeSweep(nowTicks);

        if (_entries.Count >= _maxEntries)
        {
            // One more sweep before failing closed, in case many entries just expired.
            Sweep(nowTicks);
            if (_entries.Count >= _maxEntries)
            {
                return false;
            }
        }

        return _entries.TryAdd(key, value);
    }

    /// <summary>
    /// Atomically removes and returns the entry for <paramref name="key"/> IF it exists and has not
    /// expired. One-time by construction: a second call for the same key fails. An expired entry is
    /// removed and reported as a miss.
    /// </summary>
    public bool TryConsume(string key, out TValue value)
    {
        value = default!;
        if (string.IsNullOrEmpty(key) || !_entries.TryRemove(key, out TValue? removed))
        {
            return false;
        }

        long nowTicks = _timeProvider.GetUtcNow().UtcDateTime.Ticks;
        if (_expirySelector(removed!) <= nowTicks)
        {
            // Expired — treated as absent. Already removed above.
            return false;
        }

        value = removed!;
        return true;
    }

    private void MaybeSweep(long nowTicks)
    {
        long last = Interlocked.Read(ref _lastSweepTicks);
        // Sweep at most once per ~30s to keep the hot path cheap.
        if (nowTicks - last < TimeSpan.TicksPerSecond * 30)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastSweepTicks, nowTicks, last) == last)
        {
            Sweep(nowTicks);
        }
    }

    private void Sweep(long nowTicks)
    {
        foreach (KeyValuePair<string, TValue> entry in _entries)
        {
            if (_expirySelector(entry.Value) <= nowTicks)
            {
                _entries.TryRemove(entry.Key, out _);
            }
        }
    }
}

/// <summary>
/// Non-generic helper for cryptographically random tokens, kept off the generic store type to
/// avoid declaring static members on a generic type (CA1000).
/// </summary>
public static class GitHubRandom
{
    /// <summary>
    /// Generates a new cryptographically random 256-bit key (base64url, no padding) suitable for a
    /// state value, a cookie secret, or a redemption code.
    /// </summary>
    public static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncoder.Encode(bytes.ToArray());
    }
}

/// <summary>Concrete singleton store for OAuth state entries (keyed by the random state value).</summary>
public sealed class GitHubStateStore : OneTimeTtlStore<GitHubStateEntry>
{
    public GitHubStateStore(TimeProvider? timeProvider = null)
        : base(static e => e.ExpiresAtTicks, maxEntries: 10_000, timeProvider)
    {
    }
}

/// <summary>Concrete singleton store for post-login redemption codes (keyed by the random code).</summary>
public sealed class GitHubRedemptionStore : OneTimeTtlStore<GitHubRedemptionEntry>
{
    public GitHubRedemptionStore(TimeProvider? timeProvider = null)
        : base(static e => e.ExpiresAtTicks, maxEntries: 10_000, timeProvider)
    {
    }
}

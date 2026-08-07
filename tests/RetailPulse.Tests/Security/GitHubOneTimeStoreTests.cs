using System.Collections.Concurrent;
using FluentAssertions;
using RetailPulse.Api.Security.GitHub;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Concurrency, replay, TTL and bounded-capacity contract for <see cref="OneTimeTtlStore{TValue}"/>,
/// which backs both the OAuth state store and the post-login redemption store.
///
/// These are the primitives that make callback replay, code replay, and one-time-exchange races
/// impossible, so they are proven directly:
/// <list type="bullet">
///   <item>a stored entry can be consumed exactly ONCE;</item>
///   <item>N racing consumers of the same key yield exactly ONE winner;</item>
///   <item>an expired entry is a miss and is removed;</item>
///   <item>the store is bounded and fails closed (rejects new entries) at capacity;</item>
///   <item><see cref="GitHubRandom.NewToken"/> is high-entropy and unique.</item>
/// </list>
/// </summary>
public sealed class GitHubOneTimeStoreTests
{
    private static long Ttl(TestClock clock, int seconds) =>
        clock.GetUtcNow().UtcDateTime.AddSeconds(seconds).Ticks;

    [Fact]
    public void Consume_AfterStore_SucceedsExactlyOnce()
    {
        var clock = new TestClock();
        var store = new OneTimeTtlStore<GitHubRedemptionEntry>(static e => e.ExpiresAtTicks, timeProvider: clock);
        var entry = new GitHubRedemptionEntry(new GitHubVerifiedUser(1, "octocat"), [0x01], Ttl(clock, 120));

        store.TryStore("code-1", entry).Should().BeTrue();

        store.TryConsume("code-1", out GitHubRedemptionEntry first).Should().BeTrue();
        first.User.UserId.Should().Be(1);

        // Replay: the SAME code can never be redeemed twice.
        store.TryConsume("code-1", out _).Should().BeFalse();
    }

    [Fact]
    public void Consume_UnknownKey_IsMiss()
    {
        var store = new OneTimeTtlStore<GitHubStateEntry>(static e => e.ExpiresAtTicks);

        store.TryConsume("never-stored", out _).Should().BeFalse();
    }

    [Fact]
    public void Consume_ExpiredEntry_IsMissAndRemoved()
    {
        var clock = new TestClock();
        var store = new OneTimeTtlStore<GitHubStateEntry>(static e => e.ExpiresAtTicks, timeProvider: clock);
        store.TryStore("state-1", new GitHubStateEntry([1, 2, 3], Ttl(clock, 60))).Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(61));

        store.TryConsume("state-1", out _).Should().BeFalse("the entry has expired");
        store.Count.Should().Be(0, "an expired entry is removed on access");
    }

    [Fact]
    public async Task Consume_ConcurrentRacers_YieldExactlyOneWinner()
    {
        var clock = new TestClock();
        var store = new OneTimeTtlStore<GitHubRedemptionEntry>(static e => e.ExpiresAtTicks, timeProvider: clock);
        store.TryStore("race", new GitHubRedemptionEntry(new GitHubVerifiedUser(7, "u"), [0x01], Ttl(clock, 300)));

        var winners = new ConcurrentBag<bool>();
        using var start = new Barrier(32);

        Task[] racers = [.. Enumerable.Range(0, 32).Select(i => Task.Run(() =>
        {
            _ = i;
            start.SignalAndWait();
            if (store.TryConsume("race", out _))
            {
                winners.Add(true);
            }
        }))];

        await Task.WhenAll(racers);

        winners.Count.Should().Be(1, "atomic one-time consumption admits exactly one racer");
    }

    [Fact]
    public void Store_AtCapacity_FailsClosed()
    {
        var clock = new TestClock();
        var store = new OneTimeTtlStore<GitHubStateEntry>(
            static e => e.ExpiresAtTicks, maxEntries: 3, timeProvider: clock);

        for (int i = 0; i < 3; i++)
        {
            store.TryStore($"k{i}", new GitHubStateEntry([0], Ttl(clock, 300))).Should().BeTrue();
        }

        // Capacity reached and nothing has expired → fail closed rather than evict an in-flight login.
        store.TryStore("overflow", new GitHubStateEntry([0], Ttl(clock, 300))).Should().BeFalse();
    }

    [Fact]
    public void Store_AtCapacity_SweepsExpiredThenAdmits()
    {
        var clock = new TestClock();
        var store = new OneTimeTtlStore<GitHubStateEntry>(
            static e => e.ExpiresAtTicks, maxEntries: 2, timeProvider: clock);

        store.TryStore("a", new GitHubStateEntry([0], Ttl(clock, 30))).Should().BeTrue();
        store.TryStore("b", new GitHubStateEntry([0], Ttl(clock, 30))).Should().BeTrue();

        // Both expire; a subsequent store sweeps them and succeeds (bounded + cleanup).
        clock.Advance(TimeSpan.FromSeconds(31));

        store.TryStore("c", new GitHubStateEntry([0], Ttl(clock, 30))).Should().BeTrue();
    }

    [Fact]
    public void NewToken_IsHighEntropyAndUnique()
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 1000; i++)
        {
            string token = GitHubRandom.NewToken();
            token.Length.Should().BeGreaterThanOrEqualTo(43, "a 256-bit base64url token is ~43 chars");
            token.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
            tokens.Add(token).Should().BeTrue("tokens must be unique");
        }
    }

    /// <summary>A minimal controllable <see cref="TimeProvider"/> test double (no extra package).</summary>
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}

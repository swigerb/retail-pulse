using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Persistence;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Persistence;

namespace RetailPulse.Tests.Persistence;

/// <summary>
/// Unit tests for <see cref="SqliteSessionStore"/> — the durable server-side backing for
/// issue #90 (chat session persistence). Cover the privacy-critical paths: subject
/// scoping on every read, ownership guard on write, purge with an injected clock, and
/// concurrent writes across distinct sessions.
/// </summary>
public sealed class SqliteSessionStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteSessionStore _store;

    public SqliteSessionStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"session_store_test_{Guid.NewGuid():N}.db");
        _store = new SqliteSessionStore(_dbPath, Mock.Of<ILogger<SqliteSessionStore>>());
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private static SessionTurnWrite MakeTurn(
        string sessionId,
        string subject,
        string role = "user",
        string content = "hello",
        DateTimeOffset? ts = null) =>
        new()
        {
            SessionId = sessionId,
            Subject = subject,
            TenantId = "Contoso",
            Role = role,
            Content = content,
            RoutingIntent = "chat",
            RoutingAgentKey = "generalist",
            RoutingConfidence = 0.87,
            InputTokens = 12,
            OutputTokens = 34,
            TotalTokens = 46,
            Timestamp = ts ?? DateTimeOffset.UtcNow
        };

    [Fact]
    public async Task PersistTurn_Then_GetSession_RoundTripsAllFields()
    {
        string sessionId = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await _store.PersistTurnAsync(MakeTurn(sessionId, "alice", "user", "hi", now));
        await _store.PersistTurnAsync(MakeTurn(sessionId, "alice", "assistant", "hello, alice", now.AddSeconds(1)));

        SessionDetailDto? detail = await _store.GetSessionAsync("alice", sessionId);

        detail.Should().NotBeNull();
        detail.SessionId.Should().Be(sessionId);
        detail.TenantId.Should().Be("Contoso");
        detail.Turns.Should().HaveCount(2);
        detail.Turns[0].Role.Should().Be("user");
        detail.Turns[0].Content.Should().Be("hi");
        detail.Turns[1].Role.Should().Be("assistant");
        detail.Turns[1].Content.Should().Be("hello, alice");
        detail.Turns[1].RoutingAgentKey.Should().Be("generalist");
        detail.Turns[1].TotalTokens.Should().Be(46);
    }

    [Fact]
    public async Task GetSession_ReturnsNull_ForForeignSubject()
    {
        string sessionId = Guid.NewGuid().ToString("N");
        await _store.PersistTurnAsync(MakeTurn(sessionId, "alice"));

        // Bob asking for Alice's session must resolve to null (endpoint layer surfaces 404).
        SessionDetailDto? detail = await _store.GetSessionAsync("bob", sessionId);

        detail.Should().BeNull();
    }

    [Fact]
    public async Task PersistTurn_DoesNotWrite_WhenSessionOwnedByDifferentSubject()
    {
        string sessionId = Guid.NewGuid().ToString("N");

        await _store.PersistTurnAsync(MakeTurn(sessionId, "alice", "user", "alice content"));

        // Bob tries to append to Alice's session — must be rejected silently. The
        // ownership guard rolls the transaction back so no orphan turn lands.
        await _store.PersistTurnAsync(MakeTurn(sessionId, "bob", "assistant", "sneaky reply"));

        SessionDetailDto? forAlice = await _store.GetSessionAsync("alice", sessionId);
        forAlice.Should().NotBeNull();
        forAlice.Turns.Should().HaveCount(1);
        forAlice.Turns[0].Content.Should().Be("alice content");

        SessionDetailDto? forBob = await _store.GetSessionAsync("bob", sessionId);
        forBob.Should().BeNull();
    }

    [Fact]
    public async Task ListSessions_ScopedToSubject()
    {
        await _store.PersistTurnAsync(MakeTurn(Guid.NewGuid().ToString("N"), "alice"));
        await _store.PersistTurnAsync(MakeTurn(Guid.NewGuid().ToString("N"), "alice"));
        await _store.PersistTurnAsync(MakeTurn(Guid.NewGuid().ToString("N"), "bob"));

        IReadOnlyList<SessionSummaryDto> alice = await _store.ListSessionsForSubjectAsync("alice");
        IReadOnlyList<SessionSummaryDto> bob = await _store.ListSessionsForSubjectAsync("bob");
        IReadOnlyList<SessionSummaryDto> mallory = await _store.ListSessionsForSubjectAsync("mallory");

        alice.Should().HaveCount(2);
        bob.Should().HaveCount(1);
        mallory.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteSession_RemovesTurns_AndReturnsFalseForForeignSubject()
    {
        string sessionId = Guid.NewGuid().ToString("N");
        await _store.PersistTurnAsync(MakeTurn(sessionId, "alice", "user"));
        await _store.PersistTurnAsync(MakeTurn(sessionId, "alice", "assistant"));

        // Foreign delete must fail closed.
        bool bobDelete = await _store.DeleteSessionAsync("bob", sessionId);
        bobDelete.Should().BeFalse();

        SessionDetailDto? stillThere = await _store.GetSessionAsync("alice", sessionId);
        stillThere.Should().NotBeNull();

        // Owner delete removes the whole conversation.
        bool aliceDelete = await _store.DeleteSessionAsync("alice", sessionId);
        aliceDelete.Should().BeTrue();

        SessionDetailDto? gone = await _store.GetSessionAsync("alice", sessionId);
        gone.Should().BeNull();
    }

    [Fact]
    public async Task PurgeExpired_EvictsOldSessionsAndTurns_ButKeepsRecent()
    {
        DateTimeOffset old = DateTimeOffset.UtcNow.AddDays(-40);
        DateTimeOffset recent = DateTimeOffset.UtcNow.AddMinutes(-5);

        string oldSession = Guid.NewGuid().ToString("N");
        string recentSession = Guid.NewGuid().ToString("N");

        await _store.PersistTurnAsync(MakeTurn(oldSession, "alice", "user", "old", old));
        await _store.PersistTurnAsync(MakeTurn(oldSession, "alice", "assistant", "old reply", old.AddSeconds(1)));
        await _store.PersistTurnAsync(MakeTurn(recentSession, "alice", "user", "fresh", recent));

        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        CleanupResult result = await _store.PurgeExpiredAsync(cutoff);

        result.SessionsDeleted.Should().Be(1);
        result.TurnsDeleted.Should().Be(2);

        IReadOnlyList<SessionSummaryDto> remaining = await _store.ListSessionsForSubjectAsync("alice");
        remaining.Should().HaveCount(1);
        remaining[0].SessionId.Should().Be(recentSession);
    }

    [Fact]
    public async Task ParallelWrites_ToDistinctSessions_Succeed()
    {
        // The SMB-safe pragmas (busy_timeout=10000, DELETE journal) let concurrent
        // writers cooperate through the busy handler rather than throwing SQLITE_BUSY.
        int count = 16;
        Task[] writes = [.. Enumerable.Range(0, count).Select(i =>
            _store.PersistTurnAsync(MakeTurn(
                sessionId: Guid.NewGuid().ToString("N"),
                subject: "alice",
                role: "user",
                content: $"turn-{i}")))];

        await Task.WhenAll(writes);

        IReadOnlyList<SessionSummaryDto> sessions = await _store.ListSessionsForSubjectAsync("alice");
        sessions.Should().HaveCount(count);
    }

    [Fact]
    public async Task Charts_RoundTripThroughJson_WhenPresent()
    {
        string sessionId = Guid.NewGuid().ToString("N");
        var chart = new ChartSpec
        {
            Type = "line",
            Title = "Sales",
            XAxisTitle = "week",
            YAxisTitle = "revenue"
        };
        SessionTurnWrite write = MakeTurn(sessionId, "alice", "assistant", "here you go") with
        {
            Charts = [chart]
        };
        await _store.PersistTurnAsync(write);

        SessionDetailDto? detail = await _store.GetSessionAsync("alice", sessionId);
        detail.Should().NotBeNull();
        detail.Turns[0].Charts.Should().NotBeNull();
        detail.Turns[0].Charts.Should().HaveCount(1);
        detail.Turns[0].Charts![0].Title.Should().Be("Sales");
    }

    /// <summary>
    /// Regression for the reviewer finding on PR #117: production writes the user
    /// and assistant turns with the same <c>DateTimeOffset persistNow</c> in both
    /// the cache-hit and LLM paths (<c>ChatEndpoints.cs</c>). If rehydration sorts
    /// only by <c>Timestamp</c> — with a random-GUID <c>TurnId</c> as the
    /// tie-breaker — the assistant turn will sort before the user turn about half
    /// the time. The store must guarantee strict insertion order when timestamps
    /// are identical.
    /// </summary>
    [Fact]
    public async Task GetSession_PreservesInsertionOrder_WhenTimestampsAreIdentical()
    {
        string sessionId = Guid.NewGuid().ToString("N");
        DateTimeOffset persistNow = DateTimeOffset.UtcNow;

        await _store.PersistTurnAsync(MakeTurn(sessionId, "alice", "user", "u-content", persistNow));
        await _store.PersistTurnAsync(MakeTurn(sessionId, "alice", "assistant", "a-content", persistNow));

        SessionDetailDto? detail = await _store.GetSessionAsync("alice", sessionId);

        detail.Should().NotBeNull();
        detail.Turns.Should().HaveCount(2);
        detail.Turns[0].Role.Should().Be("user", "the user turn was persisted first");
        detail.Turns[0].Content.Should().Be("u-content");
        detail.Turns[1].Role.Should().Be("assistant", "the assistant turn was persisted second");
        detail.Turns[1].Content.Should().Be("a-content");
    }

    /// <summary>
    /// Fuzz variant of the identical-timestamp regression. Repeats the same
    /// user-then-assistant persist ten times per iteration so that if the
    /// tie-breaker ever regressed to a random-GUID <c>TurnId</c> order (roughly a
    /// coin flip per pair), the probability of missing the failure is negligible
    /// (~1e-30). Keeps the guarantee honest even under lucky GUID draws.
    /// </summary>
    [Fact]
    public async Task GetSession_PreservesInsertionOrder_UnderRepeatedIdenticalTimestampPairs()
    {
        DateTimeOffset persistNow = DateTimeOffset.UtcNow;

        for (int i = 0; i < 10; i++)
        {
            string sessionId = Guid.NewGuid().ToString("N");
            await _store.PersistTurnAsync(MakeTurn(sessionId, "alice", "user", $"u-{i}", persistNow));
            await _store.PersistTurnAsync(MakeTurn(sessionId, "alice", "assistant", $"a-{i}", persistNow));

            SessionDetailDto? detail = await _store.GetSessionAsync("alice", sessionId);

            detail.Should().NotBeNull();
            detail.Turns.Should().HaveCount(2);
            detail.Turns[0].Role.Should().Be("user");
            detail.Turns[1].Role.Should().Be("assistant");
        }
    }
}

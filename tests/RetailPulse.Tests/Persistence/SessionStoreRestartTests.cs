using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Persistence;
using RetailPulse.Contracts.Persistence;

namespace RetailPulse.Tests.Persistence;

/// <summary>
/// Restart-durability test for the SQLite session store — the "browser refresh
/// after a redeploy" acceptance criterion from issue #90. Persist some turns
/// through one store instance, dispose the connection scope, then open a
/// brand-new <see cref="SqliteSessionStore"/> pointed at the same file. All rows
/// must be readable, ownership must still be enforced, and the SMB-safe pragmas
/// (<c>journal_mode=DELETE</c>, no <c>-wal</c>/<c>-shm</c> sidecars) must survive
/// the process boundary — which is what tolerating an API restart requires.
/// </summary>
public sealed class SessionStoreRestartTests : IDisposable
{
    private readonly string _dbPath;

    public SessionStoreRestartTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"session_restart_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private static SessionTurnWrite MakeTurn(string sessionId, string subject, string role, string content) =>
        new()
        {
            SessionId = sessionId,
            Subject = subject,
            TenantId = "Contoso",
            Role = role,
            Content = content,
            Timestamp = DateTimeOffset.UtcNow
        };

    [Fact]
    public async Task Sessions_Survive_A_Store_Restart()
    {
        string aliceSession = Guid.NewGuid().ToString("N");
        string bobSession = Guid.NewGuid().ToString("N");

        // First "process": write two subjects' conversations, then let the store
        // (and its connection scope) go out of scope, mimicking an API restart.
        {
            var writer = new SqliteSessionStore(_dbPath, Mock.Of<ILogger<SqliteSessionStore>>());
            await writer.PersistTurnAsync(MakeTurn(aliceSession, "alice", "user", "hello"));
            await writer.PersistTurnAsync(MakeTurn(aliceSession, "alice", "assistant", "hi alice"));
            await writer.PersistTurnAsync(MakeTurn(bobSession, "bob", "user", "bob's turn"));
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Second "process": brand-new store instance, same path.
        var reader = new SqliteSessionStore(_dbPath, Mock.Of<ILogger<SqliteSessionStore>>());

        SessionDetailDto? alice = await reader.GetSessionAsync("alice", aliceSession);
        alice.Should().NotBeNull("Alice's session must survive the restart");
        alice.Turns.Should().HaveCount(2);
        alice.Turns[0].Content.Should().Be("hello");
        alice.Turns[1].Content.Should().Be("hi alice");

        SessionDetailDto? bob = await reader.GetSessionAsync("bob", bobSession);
        bob.Should().NotBeNull("Bob's session must survive too");
        bob.Turns.Should().HaveCount(1);

        // Cross-subject probes must still fail after a restart — the ownership
        // check is in the schema (Subject column + WHERE), not in memory.
        SessionDetailDto? cross = await reader.GetSessionAsync("bob", aliceSession);
        cross.Should().BeNull("cross-subject reads must fail across restarts, not just within one process");

        // SMB-safe pragmas: DELETE journal mode leaves no persistent -wal / -shm
        // sidecars on the disk. Their presence would indicate a regression to WAL,
        // which does not survive a fresh connection on a network filesystem.
        File.Exists(_dbPath + "-wal").Should().BeFalse("DELETE journal mode leaves no persistent WAL sidecar");
        File.Exists(_dbPath + "-shm").Should().BeFalse("DELETE journal mode leaves no persistent SHM sidecar");
    }

    /// <summary>
    /// Schema-compatibility guarantee for the durable-insertion-order fix. The
    /// tie-breaker for identical timestamps is <c>rowid</c>, which every SQLite
    /// regular-rowid table already carries — no <c>ALTER TABLE</c> is needed and
    /// databases created before this change continue to order correctly on the
    /// upgraded build. Simulate that by writing a user→assistant pair with the
    /// same <c>DateTimeOffset</c> against the file, closing the store, opening a
    /// fresh <see cref="SqliteSessionStore"/> against the same path, and asserting
    /// the rehydrated transcript still returns user first, assistant second.
    /// </summary>
    [Fact]
    public async Task InsertionOrder_SurvivesRestart_ForIdenticalTimestamps()
    {
        string sessionId = Guid.NewGuid().ToString("N");
        DateTimeOffset persistNow = DateTimeOffset.UtcNow;

        {
            var writer = new SqliteSessionStore(_dbPath, Mock.Of<ILogger<SqliteSessionStore>>());
            await writer.PersistTurnAsync(new SessionTurnWrite
            {
                SessionId = sessionId,
                Subject = "alice",
                TenantId = "Contoso",
                Role = "user",
                Content = "u-restart",
                Timestamp = persistNow
            });
            await writer.PersistTurnAsync(new SessionTurnWrite
            {
                SessionId = sessionId,
                Subject = "alice",
                TenantId = "Contoso",
                Role = "assistant",
                Content = "a-restart",
                Timestamp = persistNow
            });
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var reader = new SqliteSessionStore(_dbPath, Mock.Of<ILogger<SqliteSessionStore>>());

        SessionDetailDto? detail = await reader.GetSessionAsync("alice", sessionId);
        detail.Should().NotBeNull();
        detail.Turns.Should().HaveCount(2);
        detail.Turns[0].Role.Should().Be("user", "the user turn was persisted before the assistant turn");
        detail.Turns[0].Content.Should().Be("u-restart");
        detail.Turns[1].Role.Should().Be("assistant");
        detail.Turns[1].Content.Should().Be("a-restart");
    }

    /// <summary>
    /// Backwards-compatibility guarantee: the durable-insertion-order fix relies
    /// on the intrinsic <c>rowid</c> column of a regular-rowid SQLite table (a
    /// TEXT PRIMARY KEY does not turn the table into WITHOUT ROWID). Confirm that
    /// against the actual schema materialized on disk by
    /// <see cref="SqliteSessionStore"/>: if the schema ever regressed to
    /// <c>WITHOUT ROWID</c>, the tie-breaker would silently stop being monotonic
    /// and this test would fail before any real transcript did.
    /// </summary>
    [Fact]
    public void SessionTurnsTable_IsRegularRowidTable_ForInsertionOrderTieBreak()
    {
        _ = new SqliteSessionStore(_dbPath, Mock.Of<ILogger<SqliteSessionStore>>());

        string connString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        using SqliteConnection conn = new(connString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'SessionTurns'";
        string ddl = (string)cmd.ExecuteScalar()!;

        ddl.Should().NotContain(
            "WITHOUT ROWID",
            "the store depends on rowid as the durable insertion-order tie-breaker for identical timestamps");
    }
}

using Microsoft.Data.Sqlite;

namespace RetailPulse.Api.Data;

/// <summary>
/// SMB-safe SQLite configuration for stores that live on the mounted Azure Files
/// share (audit, cost, memory, approvals, alerts).
/// <para>
/// SQLite's write-ahead log (<c>journal_mode=WAL</c>) relies on a shared-memory
/// index file (<c>-shm</c>) that is memory-mapped across connections. That
/// mechanism is unsupported on network filesystems such as Azure Files/SMB, where
/// it surfaces as intermittent <c>disk I/O error</c> / <c>database is locked</c>
/// failures. These stores therefore use a rollback journal (<c>DELETE</c>), which
/// is safe over SMB.
/// </para>
/// <para>
/// This does <b>not</b> make SQLite safe for multiple concurrent writer processes
/// over SMB. It is correct only because the API Container App runs
/// <c>maxReplicas: 1</c> (a single writer). Do not raise the replica count while
/// the durable stores share one Azure Files mount.
/// </para>
/// </summary>
public static class SqliteMount
{
    // Rollback journaling (SMB-safe), a balanced fsync policy, and an explicit
    // busy timeout so a brief lock from a concurrent reader/writer waits instead
    // of failing fast. Microsoft.Data.Sqlite also derives a busy timeout from the
    // command timeout, but setting it here documents intent for the mounted case.
    private const string _pragmas = """
        PRAGMA journal_mode=DELETE;
        PRAGMA synchronous=NORMAL;
        PRAGMA busy_timeout=10000;
        """;

    /// <summary>Apply the SMB-safe pragma set to an open connection.</summary>
    public static void ApplySmbSafePragmas(SqliteConnection connection)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = _pragmas;
        cmd.ExecuteNonQuery();
    }
}

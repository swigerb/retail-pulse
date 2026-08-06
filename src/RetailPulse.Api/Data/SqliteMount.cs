using Microsoft.Data.Sqlite;

namespace RetailPulse.Api.Data;

/// <summary>
/// Centralized SMB-safe SQLite configuration for <b>every</b> store that lives in
/// the shared writable data directory (audit, cost, memory, approvals, and alerts).
/// This is the one and only place the pragma policy is defined — individual stores
/// must not embed their own partial pragma strings.
/// <para>
/// <b>Deployment note:</b> the deployed demo does not currently mount a network
/// share — its SQLite stores live on the container's local temp disk (an
/// account-key Azure Files mount is blocked by tenant governance; see
/// <c>docs/deployment-azd.md</c>). These pragmas are nonetheless correct on local
/// disk and are retained so any future policy-compatible network-filesystem backing
/// (where WAL is unsafe) works without change.
/// </para>
/// <para>
/// SQLite's write-ahead log (<c>journal_mode=WAL</c>) relies on a shared-memory
/// index file (<c>-shm</c>) that is memory-mapped across connections. That
/// mechanism is unsupported on network filesystems such as Azure Files/SMB, where
/// it surfaces as intermittent <c>disk I/O error</c> / <c>database is locked</c>
/// failures. These stores therefore use a rollback journal (<c>DELETE</c>), which
/// is safe over SMB.
/// </para>
/// <para>
/// With a <c>DELETE</c> rollback journal the durable fsync setting per the SQLite
/// synchronous matrix is <c>synchronous=FULL</c>: <c>NORMAL</c> only guarantees
/// corruption-free crash recovery under WAL and is <em>not</em> sufficient for a
/// rollback journal, so pairing <c>DELETE</c> with <c>NORMAL</c> is the
/// inconsistency this policy avoids. FULL fsyncs the journal (and, on commit, the
/// database) so a power loss or replica kill cannot leave a torn database on the
/// share. EXTRA is not used: it only adds a directory-sync on top of FULL, which
/// SMB does not honour usefully — extra latency for no real gain here.
/// </para>
/// <para>
/// <c>busy_timeout=10000</c> (10s) is applied to <b>every</b> connection so a
/// brief lock held by a concurrent reader/writer causes a wait-and-retry instead
/// of an immediate <c>SQLITE_BUSY</c>. It is deliberately the <em>first</em>
/// pragma applied so the subsequent <c>journal_mode</c> switch — which itself
/// needs a database lock — also honours the timeout and does not throw on a fresh
/// connection that opens while another connection holds a transaction.
/// </para>
/// <para>
/// This does <b>not</b> make SQLite safe for multiple concurrent writer processes
/// over a network filesystem. It is correct only because the API Container App runs
/// <c>maxReplicas: 1</c> (a single writer). Do not raise the replica count while the
/// durable stores share one data directory.
/// </para>
/// </summary>
public static class SqliteMount
{
    // Order matters: busy_timeout is set FIRST so the journal_mode switch (which
    // takes a lock) waits instead of failing fast with SQLITE_BUSY on a
    // concurrently-opened connection. DELETE is SMB-safe (no -shm); FULL is the
    // matching durable fsync policy for a rollback journal (NORMAL is a WAL-only
    // relaxation and would be inconsistent with DELETE).
    private const string _pragmas = """
        PRAGMA busy_timeout=10000;
        PRAGMA journal_mode=DELETE;
        PRAGMA synchronous=FULL;
        """;

    /// <summary>Apply the SMB-safe pragma set to an already-open connection.</summary>
    public static void ApplySmbSafePragmas(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = _pragmas;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Open a new connection for the given connection string and apply the
    /// SMB-safe pragma set before returning it. Every per-operation connection to
    /// a mounted store must be created through this (or <see cref="OpenAsync"/>)
    /// so <c>busy_timeout</c> is in effect on <em>that</em> connection — the
    /// timeout is a per-connection setting and does not carry over from the
    /// connection that initialized the schema.
    /// </summary>
    public static SqliteConnection Open(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        ApplySmbSafePragmas(connection);
        return connection;
    }

    /// <summary>
    /// Async counterpart to <see cref="Open"/>: open a new connection and apply
    /// the SMB-safe pragma set before returning it.
    /// </summary>
    public static async Task<SqliteConnection> OpenAsync(string connectionString, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        ApplySmbSafePragmas(connection);
        return connection;
    }
}

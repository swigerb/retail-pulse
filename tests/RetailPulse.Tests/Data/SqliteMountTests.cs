using System.Globalization;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using RetailPulse.Api.Data;

namespace RetailPulse.Tests.Data;

/// <summary>
/// Tests the centralized SMB-safe SQLite policy in <see cref="SqliteMount"/> by
/// reading the <em>actual</em> PRAGMA values back from opened connections. This is
/// the single source of truth for journaling/durability on the Azure Files mount:
/// <list type="bullet">
///   <item><c>journal_mode=DELETE</c> — WAL's <c>-shm</c> file is unusable over SMB.</item>
///   <item><c>synchronous=FULL</c> — the matrix-correct durable fsync for a rollback
///   journal; <c>NORMAL</c> is a WAL-only relaxation and would be inconsistent with DELETE.</item>
///   <item><c>busy_timeout=10000</c> — applied to every connection so brief contention
///   waits instead of throwing <c>SQLITE_BUSY</c>.</item>
/// </list>
/// </summary>
public sealed class SqliteMountTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public SqliteMountTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"rp-mount-{Guid.NewGuid():N}.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string f in Directory.EnumerateFiles(
            Path.GetDirectoryName(_dbPath)!, Path.GetFileNameWithoutExtension(_dbPath) + "*"))
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
    }

    private static long ReadLong(SqliteConnection conn, string pragma)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA {pragma};";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string ReadString(SqliteConnection conn, string pragma)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA {pragma};";
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    [Fact]
    public void Open_AppliesAllThreeSmbSafePragmaValues()
    {
        using SqliteConnection conn = SqliteMount.Open(_connectionString);

        ReadString(conn, "journal_mode").Should().Be("delete", "WAL is unsafe over SMB");
        ReadLong(conn, "synchronous").Should().Be(2, "synchronous=FULL (2) is the durable setting for a DELETE journal");
        ReadLong(conn, "busy_timeout").Should().Be(10_000, "every connection must wait out brief locks");
    }

    [Fact]
    public async Task OpenAsync_AppliesAllThreeSmbSafePragmaValues()
    {
        await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString);

        ReadString(conn, "journal_mode").Should().Be("delete");
        ReadLong(conn, "synchronous").Should().Be(2);
        ReadLong(conn, "busy_timeout").Should().Be(10_000);
    }

    [Fact]
    public void ApplySmbSafePragmas_SetsValuesOnAnExistingConnection()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        SqliteMount.ApplySmbSafePragmas(conn);

        ReadLong(conn, "synchronous").Should().Be(2);
        ReadLong(conn, "busy_timeout").Should().Be(10_000);
        ReadString(conn, "journal_mode").Should().Be("delete");
    }

    [Fact]
    public void PolicyIsNeverNormalSynchronous()
    {
        // Guards against regressing to the inconsistent journal_mode=DELETE +
        // synchronous=NORMAL pairing.
        using SqliteConnection conn = SqliteMount.Open(_connectionString);
        ReadLong(conn, "synchronous").Should().NotBe(1, "NORMAL is inconsistent with a DELETE rollback journal");
    }
}

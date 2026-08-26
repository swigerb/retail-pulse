using System.Text;
using Microsoft.Data.Sqlite;

namespace RetailPulse.Tests.TestInfrastructure;

/// <summary>
/// The single shared owner of SQLite temp-file lifetime for every test in this
/// project. Every fixture that opens a SQLite database MUST allocate its path
/// through <see cref="NewDbPath(string)"/> and release it through
/// <see cref="ReleaseAndDelete(string)"/> (or the batch overload).
///
/// <para>
/// Issue #158 documented ~280,497 orphaned <c>*.db*</c> files (~1.36 TB) in
/// <c>%TEMP%</c> accumulated by repeated local runs. The root cause was
/// <see cref="SqliteConnection"/> pooling: the stores under test open per-
/// operation connections via <c>await using</c>, which returns the underlying
/// handle to the pool rather than closing it. Fixture <c>Dispose</c> then
/// tried to <c>File.Delete</c> the database path while the pool still held
/// the OS handle open — Windows returned <c>ERROR_SHARING_VIOLATION</c>,
/// the delete was silently swallowed by <c>try { ... } catch { }</c>, and
/// every failed test run leaked a database plus (for WAL / rollback journals)
/// a matching <c>-wal</c>, <c>-shm</c>, or <c>-journal</c> sidecar.
/// </para>
///
/// <para>
/// The contract this helper enforces:
/// </para>
/// <list type="number">
///   <item>Every test DB lives under <see cref="TempRoot"/> — a dedicated
///     namespace-scoped subdirectory so a leak count is measurable without
///     unrelated <c>%TEMP%</c> noise (see PR body for the counting
///     methodology).</item>
///   <item><see cref="ReleaseAndDelete(string)"/> calls
///     <see cref="SqliteConnection.ClearPool"/> using the exact connection
///     string shape the stores use (<c>Mode=ReadWriteCreate</c>,
///     <c>Cache=Shared</c>) before any <c>File.Delete</c>. That releases the
///     pooled handle, so the delete succeeds instead of failing silently.</item>
///   <item>Every SQLite sidecar (<c>-journal</c>, <c>-wal</c>, <c>-shm</c>)
///     is deleted, not just the main <c>.db</c>. Even though <c>SqliteMount</c>
///     runs <c>journal_mode=DELETE</c> (no <c>-wal</c> / <c>-shm</c> in normal
///     operation) the sidecars are removed defensively so a future policy
///     change cannot silently reintroduce a leak.</item>
///   <item>Deletion failures throw. They are the sentinel that says a pooled
///     connection is still holding the file — the exact bug that produced the
///     original leak. Callers <b>must not</b> reinstate a <c>catch</c>-and-
///     ignore around <see cref="ReleaseAndDelete(string)"/>.</item>
/// </list>
///
/// <para>
/// Disposal ordering: fixture <c>Dispose</c> must dispose its store handles
/// (if any) <em>before</em> calling this helper. This helper never touches
/// caller-owned <see cref="SqliteConnection"/> instances, so it cannot
/// reintroduce the SafeHandle / <see cref="ObjectDisposedException"/> race
/// audited in issue #156 — it only creates a fresh, never-opened
/// <see cref="SqliteConnection"/> to satisfy the <see cref="SqliteConnection.ClearPool"/>
/// signature (which reads the connection string off the instance).
/// </para>
/// </summary>
public static class SqliteTestCleanup
{
    /// <summary>
    /// Namespace-scoped root for every SQLite temp file the test suite creates.
    /// Isolated from ambient <c>%TEMP%</c> so the leak counter documented in the
    /// PR body has a precise, exclusive matching pattern.
    /// </summary>
    public static readonly string TempRoot = Path.Combine(
        Path.GetTempPath(), "RetailPulse.Tests.Sqlite");

    /// <summary>
    /// Allocate a fresh, guaranteed-unique database path under
    /// <see cref="TempRoot"/>. Callers pass <paramref name="label"/> so a
    /// mid-run inspection of the directory names the owning fixture.
    /// </summary>
    public static string NewDbPath(string label)
    {
        Directory.CreateDirectory(TempRoot);
        return Path.Combine(TempRoot, $"{Sanitize(label)}_{Guid.NewGuid():N}.db");
    }

    /// <summary>
    /// Release the pooled SQLite handle for <paramref name="dbPath"/>, delete
    /// the database file, and delete every SQLite sidecar. Throws on any
    /// deletion failure — a lock survives here only if a caller-owned
    /// connection was not disposed first, which is the exact regression #158
    /// is preventing.
    /// </summary>
    public static void ReleaseAndDelete(string dbPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(dbPath);

        // 1) Release the pooled handle for the exact connection string every
        //    store in this project uses. Do this BEFORE any File.Delete so the
        //    OS handle is closed and the delete can succeed on Windows.
        var probe = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        SqliteConnection.ClearPool(probe);

        // 2) A handful of tests build a simpler `Data Source=<path>` string for
        //    read-back verification (PRAGMA reads, seeded-row checks). That
        //    lands in a different pool bucket, so clear that shape too.
        var probeSimple = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
        }.ToString());
        SqliteConnection.ClearPool(probeSimple);

        // 3) Delete the database + every SQLite sidecar. Sidecars are:
        //      -journal : DELETE rollback journal (the mode SqliteMount uses)
        //      -wal / -shm : write-ahead log + shared-memory index (not used
        //                    by our policy, but deleted defensively so a
        //                    future WAL flip cannot silently regress the leak).
        List<Exception>? errors = null;
        foreach (string path in Sidecars(dbPath))
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(new IOException(
                    $"Could not delete '{path}' — a pooled or live SqliteConnection " +
                    "is still holding the file open. Dispose the store BEFORE calling " +
                    "ReleaseAndDelete, and never wrap this call in a swallowing catch " +
                    "(see issue #158 for the leak this guards against).", ex));
            }
        }

        if (errors is { Count: > 0 })
        {
            throw new AggregateException(
                $"SqliteTestCleanup.ReleaseAndDelete failed for '{dbPath}'.",
                errors);
        }
    }

    /// <summary>
    /// Batch version for fixtures that own more than one database (for example
    /// tests that instantiate both a memory store and an approval gate).
    /// Every path is attempted even if an earlier one throws so a single
    /// stubborn lock cannot mask a leak on a sibling path; failures are
    /// aggregated into a single throw.
    /// </summary>
    public static void ReleaseAndDelete(params string[] dbPaths)
    {
        ArgumentNullException.ThrowIfNull(dbPaths);
        List<Exception>? errors = null;
        foreach (string p in dbPaths)
        {
            try { ReleaseAndDelete(p); }
            catch (Exception ex) { (errors ??= []).Add(ex); }
        }
        if (errors is { Count: > 0 })
            throw new AggregateException("One or more SQLite temp cleanups failed.", errors);
    }

    /// <summary>
    /// Every path SQLite may create alongside <paramref name="dbPath"/>. The
    /// enumeration is public so the helper's own tests can assert every one
    /// of them is gone after cleanup.
    /// </summary>
    public static IEnumerable<string> Sidecars(string dbPath)
    {
        yield return dbPath;
        yield return dbPath + "-journal";
        yield return dbPath + "-wal";
        yield return dbPath + "-shm";
    }

    private static string Sanitize(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "db";
        char[] invalid = Path.GetInvalidFileNameChars();
        var buf = new StringBuilder(label.Length);
        foreach (char c in label)
            buf.Append(invalid.Contains(c) ? '_' : c);
        return buf.ToString();
    }
}

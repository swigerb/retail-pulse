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
///     <see cref="SqliteConnection.ClearPool"/> for the specific
///     <c>Data Source=&lt;path&gt;</c> across every connection-string
///     variant tests are known to open (RWCreate+Shared for the stores,
///     ReadOnly+Shared and plain ReadOnly for verification reads, and
///     bare <c>Data Source=&lt;path&gt;</c>). Path-scoped clearing is
///     used instead of
///     <see cref="SqliteConnection.ClearAllPools"/> so it cannot race
///     with a sibling xUnit fixture running in parallel — every fixture
///     allocates a unique DB path, so per-path clearing only touches
///     handles this fixture owns. A process-wide clear would reproduce
///     the SafeHandle / <see cref="ObjectDisposedException"/> race
///     audited in issue #156.</item>
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
/// caller-owned <see cref="SqliteConnection"/> instances — it never
/// constructs or opens one — so it cannot reintroduce the SafeHandle /
/// <see cref="ObjectDisposedException"/> race audited in issue #156.
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

        // 1) Release every pooled handle keyed on THIS specific path. Pool
        //    buckets are keyed on the exact connection string, so we clear
        //    each variant tests are known to open against a temp DB:
        //      • RWCreate + Shared cache : the shape every store builds via
        //        SqliteMount (SqliteApprovalGate, SqlitePlanStore,
        //        SqliteSessionStore, SqliteAlertService, SqliteConversationMemory,
        //        RetailPulseDb).
        //      • ReadOnly  + Shared cache : verification reads in
        //        DemandDataTests, CompetitiveDataTests, PromoDataTests,
        //        SupplyDataTests, StoreDataTests, MarginDataTests.
        //      • ReadOnly (no cache)     : StoreOpsToolTests, PlanogramTests
        //        ("Data Source=<path>;Mode=ReadOnly").
        //      • Bare "Data Source=<path>" : DurableCostTracker,
        //        DurableAuditLog, PackSwitchSeedDimensionsTests,
        //        DefaultPackSeedGoldenTests, MountedStorePragmaContractTests.
        //    Missing any one of these leaves an OS handle open on Windows
        //    and the delete fails — which is the exact leak this helper
        //    guards against (#158).
        //
        //    Per-path clearing is chosen over SqliteConnection.ClearAllPools()
        //    on purpose: xUnit v2 runs fixtures in parallel in the same
        //    process, and ClearAllPools disposes idle handles across every
        //    pool bucket. If a sibling fixture just checked out one of
        //    those handles, the process-wide clear races with its next
        //    Open and reproduces the SafeHandle / ObjectDisposedException
        //    from issue #156. Path-scoped clearing cannot race with a
        //    sibling because every fixture allocates a unique DB path.
        ClearPoolForConnectionString(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString());

        ClearPoolForConnectionString(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
        }.ToString());

        ClearPoolForConnectionString(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());

        ClearPoolForConnectionString(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
        }.ToString());

        // 2) Delete the database + every SQLite sidecar. Sidecars are:
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
    /// Clears the Microsoft.Data.Sqlite pool bucket keyed on the given
    /// connection string. Uses a fresh, never-opened
    /// <see cref="SqliteConnection"/> purely as a carrier for the string —
    /// <see cref="SqliteConnection.ClearPool"/> reads its
    /// <see cref="SqliteConnection.ConnectionString"/> to locate the
    /// bucket. The carrier is never opened and its underlying
    /// <c>SQLitePCL.sqlite3</c> is never referenced, so this cannot
    /// reintroduce the SafeHandle disposal race audited in issue #156.
    /// </summary>
    private static void ClearPoolForConnectionString(string connectionString)
    {
        var probe = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(probe);
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

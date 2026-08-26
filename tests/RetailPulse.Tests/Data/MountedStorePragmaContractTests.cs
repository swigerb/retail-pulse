using System.Globalization;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using RetailPulse.Api.Alerts;
using RetailPulse.Api.Approval;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Memory;
using RetailPulse.Api.Observability;
using RetailPulse.Api.Security;
using RetailPulse.Tests.TestInfrastructure;

namespace RetailPulse.Tests.Data;

/// <summary>
/// Enumerates <b>every</b> SQLite store that opens a database under the shared
/// durable data directory and asserts each one goes through the centralized
/// SMB-safe pragma policy (<see cref="Api.Data.SqliteMount"/>) rather
/// than embedding its own partial PRAGMA string. Combines:
/// <list type="number">
///   <item>a behavioral check that each store's on-disk database uses the SMB-safe
///   <c>journal_mode=DELETE</c> (never WAL, whose <c>-shm</c> file is unusable over
///   SMB); and</item>
///   <item>a source contract that catches a <em>missing</em> pragma call — any file
///   that opens a <c>SqliteConnection</c> must route through <c>SqliteMount</c>, and
///   no store may hard-code its own <c>PRAGMA</c> journaling/synchronous/busy_timeout.</item>
/// </list>
/// The second check is what protects a newly-added store: if someone adds a store
/// that opens its own connection without the shared helper, this fails.
/// </summary>
public sealed class MountedStorePragmaContractTests : IDisposable
{
    private static readonly string RepoRoot = FindRepoRoot();
    private readonly List<string> _dbPaths = [];

    public void Dispose()
    {
        SqliteTestCleanup.ReleaseAndDelete([.. _dbPaths]);
    }

    private string NewDbPath(string label)
    {
        string p = SqliteTestCleanup.NewDbPath($"rp-{label}");
        _dbPaths.Add(p);
        return p;
    }

    /// <summary>Factory per mounted store: constructs it (which opens + initializes the DB) and disposes.</summary>
    private (string Name, Action<string> Build)[] MountedStores() =>
    [
        ("DurableAuditLog", p => { using var s = new DurableAuditLog(p); }),
        ("DurableCostTracker", p =>
        {
            using var s = new DurableCostTracker(
                p, Options.Create(new ObservabilityOptions()), new ConfigurationBuilder().Build());
        }),
        ("SqliteApprovalGate", p => _ = new SqliteApprovalGate(p, Mock.Of<ILogger<SqliteApprovalGate>>())),
        ("SqliteConversationMemory", p =>
        {
            using var s = new SqliteConversationMemory(p, Mock.Of<ILogger<SqliteConversationMemory>>());
        }),
        ("SqliteAlertService", p =>
        {
            using var s = new SqliteAlertService(p, Mock.Of<ILogger<SqliteAlertService>>());
        }),
    ];

    [Fact]
    public void EveryMountedStore_PersistsWithSmbSafeDeleteJournal()
    {
        var offenders = new List<string>();

        foreach ((string name, Action<string> build) in MountedStores())
        {
            string dbPath = NewDbPath(name);
            build(dbPath);

            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
            conn.Open();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            string mode = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;

            if (!string.Equals(mode, "delete", StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add($"{name} -> journal_mode={mode}");
            }
        }

        offenders.Should().BeEmpty(
            "every mounted SQLite store must use the SMB-safe DELETE rollback journal (WAL is unusable over Azure Files)");
    }

    [Fact]
    public void EveryFileThatOpensSqlite_RoutesThroughSqliteMount()
    {
        string apiSrc = Path.Combine(RepoRoot, "src", "RetailPulse.Api");
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(apiSrc, "*.cs", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(file), "SqliteMount.cs", StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            if (text.Contains("new SqliteConnection(", StringComparison.Ordinal)
                && !text.Contains("SqliteMount", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetRelativePath(apiSrc, file));
            }
        }

        offenders.Should().BeEmpty(
            "every file that opens a SqliteConnection must apply the centralized SMB-safe pragmas via SqliteMount — " +
            "a store that opens its own connection without busy_timeout can throw SQLITE_BUSY on contention");
    }

    [Fact]
    public void NoMountedStore_HardCodesItsOwnJournalingPragmas()
    {
        string apiSrc = Path.Combine(RepoRoot, "src", "RetailPulse.Api");
        string[] banned = ["PRAGMA journal_mode", "PRAGMA synchronous", "PRAGMA busy_timeout"];
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(apiSrc, "*.cs", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(file), "SqliteMount.cs", StringComparison.Ordinal))
            {
                continue; // the single, centralized source of the pragma policy
            }

            string text = File.ReadAllText(file);
            foreach (string pragma in banned)
            {
                if (text.Contains(pragma, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{Path.GetRelativePath(apiSrc, file)} contains '{pragma}'");
                }
            }
        }

        offenders.Should().BeEmpty(
            "journaling/synchronous/busy_timeout pragmas must live only in SqliteMount; duplicated partial pragma " +
            "strings drift out of sync with the centralized policy");
    }

    [Fact]
    public void CentralPolicy_AppliesBusyTimeoutBeforeJournalModeSwitch()
    {
        // Ordering matters: the journal_mode switch takes a lock, so busy_timeout
        // must be set first or a fresh connection can throw SQLITE_BUSY during init.
        string mount = File.ReadAllText(Path.Combine(RepoRoot, "src", "RetailPulse.Api", "Data", "SqliteMount.cs"));

        int busy = mount.IndexOf("PRAGMA busy_timeout", StringComparison.Ordinal);
        int journal = mount.IndexOf("PRAGMA journal_mode", StringComparison.Ordinal);
        int sync = mount.IndexOf("PRAGMA synchronous", StringComparison.Ordinal);

        busy.Should().BeGreaterThanOrEqualTo(0);
        journal.Should().BeGreaterThan(busy, "busy_timeout must precede the journal_mode switch");
        mount.Should().Contain("PRAGMA synchronous=FULL", "the policy must use synchronous=FULL with a DELETE journal");
        sync.Should().BeGreaterThan(0);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RetailPulse.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate repo root (RetailPulse.slnx) walking up from " + AppContext.BaseDirectory);
    }
}

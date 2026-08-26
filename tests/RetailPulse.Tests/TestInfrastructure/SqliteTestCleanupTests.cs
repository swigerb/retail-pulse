using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Approval;
using RetailPulse.Api.Memory;
using RetailPulse.Contracts.Approval;
using RetailPulse.Contracts.Memory;

namespace RetailPulse.Tests.TestInfrastructure;

/// <summary>
/// Regression guard for the SQLite temp-file leak documented in issue #158.
/// These tests pin the contract every fixture in this project relies on: the
/// shared <see cref="SqliteTestCleanup"/> helper releases the pooled handle
/// before deletion, removes every SQLite sidecar (not just the main .db),
/// and surfaces (rather than swallows) a lock. If any of these turn red,
/// the ~280 k / 1.36 TB leak that motivated the fix is back.
/// </summary>
public sealed class SqliteTestCleanupTests
{
    [Fact]
    public void TempRoot_LivesUnderTempAndIsNamespaceScoped()
    {
        // The counting methodology documented on the PR only holds if every
        // test's DB lives under a directory the suite owns exclusively.
        SqliteTestCleanup.TempRoot.Should().StartWith(Path.GetTempPath());
        Path.GetFileName(SqliteTestCleanup.TempRoot)
            .Should().Be("RetailPulse.Tests.Sqlite");
    }

    [Fact]
    public void NewDbPath_ProducesUniquePathsUnderTempRoot()
    {
        string a = SqliteTestCleanup.NewDbPath("alpha");
        string b = SqliteTestCleanup.NewDbPath("alpha");

        a.Should().NotBe(b);
        Path.GetDirectoryName(a).Should().Be(SqliteTestCleanup.TempRoot);
        Path.GetDirectoryName(b).Should().Be(SqliteTestCleanup.TempRoot);
        Path.GetFileName(a).Should().StartWith("alpha_");
        Path.GetExtension(a).Should().Be(".db");

        // NewDbPath must be idempotent about directory creation.
        Directory.Exists(SqliteTestCleanup.TempRoot).Should().BeTrue();
    }

    [Fact]
    public void ReleaseAndDelete_RemovesDbAndEverySidecar_WhenPresent()
    {
        string dbPath = SqliteTestCleanup.NewDbPath("sidecar-sweep");
        // Simulate every sidecar SQLite might leave behind so the helper's
        // sidecar sweep is proved end-to-end, not just claimed.
        foreach (string p in SqliteTestCleanup.Sidecars(dbPath))
            File.WriteAllBytes(p, [0x1]);

        SqliteTestCleanup.ReleaseAndDelete(dbPath);

        foreach (string p in SqliteTestCleanup.Sidecars(dbPath))
            File.Exists(p).Should().BeFalse($"sidecar '{p}' must be swept");
    }

    [Fact]
    public void ReleaseAndDelete_IsNoOpForNonExistentPath()
    {
        string missing = Path.Combine(SqliteTestCleanup.TempRoot,
            $"never-created-{Guid.NewGuid():N}.db");

        Action act = () => SqliteTestCleanup.ReleaseAndDelete(missing);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task ReleaseAndDelete_AfterRealStoreUse_RemovesEverythingLeftInTempRoot()
    {
        // End-to-end proof against the exact class of leak: use the actual
        // SqliteConversationMemory + SqliteApprovalGate stores (which open
        // per-operation connections through the pool), then run the helper
        // and prove the DB is really gone. If the pool still held the file
        // handle open, File.Delete would have failed on Windows, the helper
        // would have thrown, and the test would fail — which is precisely
        // the regression guard #158 asks for.
        string memPath = SqliteTestCleanup.NewDbPath("regression-memory");
        string apprPath = SqliteTestCleanup.NewDbPath("regression-approval");

        var memory = new SqliteConversationMemory(memPath, NullLogger<SqliteConversationMemory>.Instance);
        var gate = new SqliteApprovalGate(apprPath, NullLogger<SqliteApprovalGate>.Instance);

        await memory.StoreAsync("user-1", new MemoryEntry(
            Id: Guid.NewGuid().ToString("N"),
            UserId: "user-1",
            Type: MemoryType.ConversationSummary,
            Content: "regression content",
            EntityKey: null,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(1)));

        ApprovalRequest req = await gate.RequestApprovalAsync(new ApprovalContext(
            "agent-1", "user-1", "regression action", "Low", "medium", "Testing"));
        await gate.RespondAsync(req.RequestId, ApprovalDecision.Approved);

        memory.Dispose();

        SqliteTestCleanup.ReleaseAndDelete(memPath, apprPath);

        foreach (string p in SqliteTestCleanup.Sidecars(memPath).Concat(SqliteTestCleanup.Sidecars(apprPath)))
            File.Exists(p).Should().BeFalse($"'{p}' must be gone after ReleaseAndDelete — otherwise #158 is back");
    }

    [Fact]
    public void ReleaseAndDelete_ThrowsWhenFileIsLocked_InsteadOfSilentlyLeaking()
    {
        // Simulate the exact failure mode #158 was silently swallowing:
        // something else holds the file open when Dispose runs. The helper
        // MUST surface this, not catch-and-ignore. If a future refactor puts
        // a `try { ... } catch { }` back around ReleaseAndDelete's File.Delete,
        // this test turns red.
        string dbPath = SqliteTestCleanup.NewDbPath("locked-file");
        File.WriteAllBytes(dbPath, [0x0]);

        // A non-shared FileStream keeps the OS handle open with a deny-all
        // sharing mode — File.Delete against a file held this way throws on
        // Windows.
        using (FileStream _ = new(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Action act = () => SqliteTestCleanup.ReleaseAndDelete(dbPath);
            act.Should().Throw<AggregateException>(
                "a locked file is the sentinel that a live handle survived — the helper MUST surface it")
               .WithInnerException<IOException>();
        }

        // After the lock is released the helper cleans up cleanly.
        SqliteTestCleanup.ReleaseAndDelete(dbPath);
        File.Exists(dbPath).Should().BeFalse();
    }

    [Fact]
    public void ReleaseAndDelete_Batch_AggregatesFailuresAcrossPaths()
    {
        // A single leak on one path must not mask a leak on a sibling path.
        // Prove the batch overload attempts every path and aggregates failures.
        string good = SqliteTestCleanup.NewDbPath("batch-good");
        string bad = SqliteTestCleanup.NewDbPath("batch-locked");
        File.WriteAllBytes(good, [0x1]);
        File.WriteAllBytes(bad, [0x1]);

        using (FileStream _ = new(bad, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Action act = () => SqliteTestCleanup.ReleaseAndDelete(good, bad);
            act.Should().Throw<AggregateException>();
        }

        // The unlocked path was still cleaned up.
        File.Exists(good).Should().BeFalse();

        // Follow-up call clears the (now-unlocked) sibling.
        SqliteTestCleanup.ReleaseAndDelete(bad);
        File.Exists(bad).Should().BeFalse();
    }

    [Fact]
    public void ReleaseAndDelete_DoesNotTouchCallerOwnedSqliteConnection_NoObjectDisposedRace()
    {
        // Guards against reintroducing the SafeHandle / ObjectDisposedException
        // race audited in issue #156: the helper opens NO caller-owned handle.
        // The connection it constructs to satisfy SqliteConnection.ClearPool's
        // signature is a fresh, never-opened instance — proving that here so
        // a future "helpful" refactor cannot silently start disposing shared
        // state.
        string dbPath = SqliteTestCleanup.NewDbPath("no-shared-handle");

        // A caller opens their own connection, uses it, disposes it. The
        // helper must be a no-op on THAT handle — the caller controls its
        // lifetime.
        using (var owned = new SqliteConnection($"Data Source={dbPath}"))
        {
            owned.Open();
            using SqliteCommand cmd = owned.CreateCommand();
            cmd.CommandText = "CREATE TABLE T (X INTEGER)";
            cmd.ExecuteNonQuery();
        } // owned disposed here — pool now holds the underlying handle

        // After caller disposal, the helper must be able to fully clean up
        // without touching the caller's already-disposed reference.
        SqliteTestCleanup.ReleaseAndDelete(dbPath);
        File.Exists(dbPath).Should().BeFalse();
    }
}

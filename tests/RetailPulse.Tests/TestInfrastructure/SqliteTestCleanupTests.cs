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
    public void ReleaseAndDelete_SurfacesDeletionFailures_InsteadOfSilentlyLeaking()
    {
        // Pins the "never swallow a delete failure" contract that #158 exists
        // to enforce. If a future refactor reintroduces a `try { ... } catch { }`
        // around ReleaseAndDelete's File.Delete, this test turns red.
        //
        // Portable forced-failure design:
        //   The helper's delete loop is `if (File.Exists(path)) File.Delete(path)`,
        //   and File.Exists returns false for a directory, so a "directory
        //   sitting at the target path" would be silently skipped — no throw,
        //   no guard. Instead we place a REAL file at the target and make
        //   File.Delete fail against it portably:
        //     • Windows: mark the file read-only. File.Delete on a read-only
        //       file surfaces UnauthorizedAccessException (which the helper
        //       wraps in IOException). No admin rights required.
        //     • Linux: strip write permission from the file's parent directory.
        //       POSIX unlink(2) requires write on the parent, so the delete
        //       returns EACCES (surfaced as UnauthorizedAccessException /
        //       IOException). Isolating the guard in a dedicated subdirectory
        //       means no sibling test's cleanup is affected.
        //   Both platforms end up in the helper's catch wrapper and observe
        //   the same AggregateException / inner-IOException shape.
        //
        // The original Windows sharing-violation shape is preserved as an
        // OPTIONAL supplemental test below, guarded by OperatingSystem.IsWindows(),
        // so we still exercise the exact ERROR_SHARING_VIOLATION path #158
        // originated from on the platform where it applies.
        Directory.CreateDirectory(SqliteTestCleanup.TempRoot);
        string guardDir = Path.Combine(SqliteTestCleanup.TempRoot, $"guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(guardDir);
        string dbPath = Path.Combine(guardDir, "undeletable.db");
        File.WriteAllBytes(dbPath, [0x1]);

        try
        {
            MakeUndeletable(guardDir, dbPath);

            Action act = () => SqliteTestCleanup.ReleaseAndDelete(dbPath);
            act.Should().Throw<AggregateException>(
                "an undeletable target is the sentinel that a leak survived — the helper MUST surface it")
               .WithInnerException<IOException>();
        }
        finally
        {
            // Teardown in finally so this test itself cannot leak the guard
            // directory into %TEMP%\RetailPulse.Tests.Sqlite\ even if an
            // assertion above threw.
            RestoreAndCleanup(guardDir, dbPath);
        }
    }

    [Fact]
    public void ReleaseAndDelete_Batch_AggregatesFailuresAcrossPaths()
    {
        // A single failure on one path must not mask a failure on a sibling
        // path — and the good sibling MUST still be cleaned up. Uses the same
        // portable read-only-file / non-writable-parent guard as the primary
        // test so the "bad" path fails deletion on Windows and Linux identically.
        string good = SqliteTestCleanup.NewDbPath("batch-good");
        File.WriteAllBytes(good, [0x1]);

        Directory.CreateDirectory(SqliteTestCleanup.TempRoot);
        string guardDir = Path.Combine(SqliteTestCleanup.TempRoot, $"guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(guardDir);
        string bad = Path.Combine(guardDir, "batch-undeletable.db");
        File.WriteAllBytes(bad, [0x1]);

        try
        {
            MakeUndeletable(guardDir, bad);

            Action act = () => SqliteTestCleanup.ReleaseAndDelete(good, bad);
            act.Should().Throw<AggregateException>();

            // The good sibling was still cleaned up — the batch overload
            // does not short-circuit on the first failure.
            File.Exists(good).Should().BeFalse();
        }
        finally
        {
            RestoreAndCleanup(guardDir, bad);
            if (File.Exists(good))
                File.Delete(good);
        }
    }

    [Fact]
    public void ReleaseAndDelete_ThrowsWhenFileIsLocked_OnWindows_SharingViolation()
    {
        // Supplemental Windows-only fidelity check: on Windows the ORIGINAL
        // #158 failure mode was ERROR_SHARING_VIOLATION from a pooled
        // SqliteConnection still holding the OS handle open. This test
        // reproduces that exact shape with a deny-all FileStream so a future
        // refactor cannot regress the Windows-specific surface without
        // turning something red.
        //
        // This test is intentionally Windows-only: on Linux, POSIX unlink(2)
        // permits removing an open file, so there is no equivalent "locked
        // file blocks delete" behaviour to pin — writing a Linux variant
        // that pretends otherwise would misrepresent the platform.
        if (!OperatingSystem.IsWindows())
            return;

        string dbPath = SqliteTestCleanup.NewDbPath("locked-file-windows");
        File.WriteAllBytes(dbPath, [0x0]);

        using (FileStream _ = new(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Action act = () => SqliteTestCleanup.ReleaseAndDelete(dbPath);
            act.Should().Throw<AggregateException>(
                "on Windows a locked file is the exact ERROR_SHARING_VIOLATION shape #158 originated from")
               .WithInnerException<IOException>();
        }

        // After the lock is released the helper cleans up cleanly.
        SqliteTestCleanup.ReleaseAndDelete(dbPath);
        File.Exists(dbPath).Should().BeFalse();
    }

    private static void MakeUndeletable(string parentDir, string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            // File.Delete on a read-only file surfaces UnauthorizedAccessException
            // on Windows — enough to prove the helper does not swallow it.
            File.SetAttributes(filePath, FileAttributes.ReadOnly);
        }
        else
        {
            // POSIX unlink(2) requires write permission on the parent
            // directory. Stripping write from parentDir forces File.Delete
            // to return EACCES (surfaced as UnauthorizedAccessException).
            // Read + execute retained so File.Exists can still resolve the
            // file inside.
            File.SetUnixFileMode(parentDir,
                UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }

    private static void RestoreAndCleanup(string parentDir, string filePath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(filePath))
                    File.SetAttributes(filePath, FileAttributes.Normal);
            }
            else if (Directory.Exists(parentDir))
            {
                File.SetUnixFileMode(parentDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch
        {
            // Best-effort — teardown must never mask the real assertion.
        }

        if (Directory.Exists(parentDir))
        {
            try { Directory.Delete(parentDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ReleaseAndDelete_DoesNotTouchCallerOwnedSqliteConnection_NoObjectDisposedRace()
    {
        // Guards against reintroducing the SafeHandle / ObjectDisposedException
        // race audited in issue #156: the helper only builds fresh, never-opened
        // SqliteConnection instances as probes for SqliteConnection.ClearPool,
        // and it only clears buckets keyed on THIS fixture's unique db path —
        // so it can never touch the caller-owned handle above. And because
        // clearing is path-scoped (never SqliteConnection.ClearAllPools()), a
        // parallel xUnit fixture cannot have its live sqlite3 handle disposed
        // under it either.
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

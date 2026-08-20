using System.Globalization;
using Microsoft.Data.Sqlite;
using RetailPulse.Api.Data;
using RetailPulse.Contracts.Persistence;

namespace RetailPulse.Api.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="IPlanStore"/>. Sibling of
/// <see cref="SqliteSessionStore"/> — same shared-cache connection string, same
/// SMB-safe pragmas via <see cref="SqliteMount"/>, same subject-scoped read
/// contract. Plans and steps live in two tables joined by <c>PlanId</c>; every
/// read enforces the caller's subject in the WHERE clause so a cross-subject
/// probe cannot leak plan content.
/// </summary>
public sealed class SqlitePlanStore : IPlanStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqlitePlanStore> _logger;

    private const string _iso8601 = "yyyy-MM-ddTHH:mm:ss.fffzzz";
    private static readonly char _intentSeparator = '\u001F';

    public SqlitePlanStore(string dbPath, ILogger<SqlitePlanStore> logger)
    {
        _logger = logger;

        string? dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        InitializeSchema();

        _logger.LogInformation("Plan store initialized at {DbPath}", dbPath);
    }

    // ── Schema ───────────────────────────────────────────────────────────

    private void InitializeSchema()
    {
        using SqliteConnection conn = SqliteMount.Open(_connectionString);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Plans (
                PlanId              TEXT    PRIMARY KEY,
                Subject             TEXT    NOT NULL COLLATE NOCASE,
                SessionId           TEXT,
                TenantId            TEXT,
                Request             TEXT    NOT NULL,
                DetectedIntents     TEXT    NOT NULL DEFAULT '',
                Status              TEXT    NOT NULL,
                FailureReason       TEXT,
                TotalInputTokens    INTEGER,
                TotalOutputTokens   INTEGER,
                TotalTokens         INTEGER,
                TotalDurationMs     INTEGER,
                CreatedAt           TEXT    NOT NULL,
                UpdatedAt           TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Plans_Subject_UpdatedAt
                ON Plans (Subject, UpdatedAt DESC);

            CREATE INDEX IF NOT EXISTS IX_Plans_UpdatedAt
                ON Plans (UpdatedAt);

            CREATE TABLE IF NOT EXISTS PlanSteps (
                StepId              TEXT    PRIMARY KEY,
                PlanId              TEXT    NOT NULL,
                StepIndex           INTEGER NOT NULL,
                SpecialistKey       TEXT    NOT NULL,
                Intent              TEXT    NOT NULL,
                Action              TEXT    NOT NULL,
                Status              TEXT    NOT NULL,
                Result              TEXT,
                Error               TEXT,
                InputTokens         INTEGER,
                OutputTokens        INTEGER,
                TotalTokens         INTEGER,
                DurationMs          INTEGER,
                StartedAt           TEXT,
                CompletedAt         TEXT,
                FOREIGN KEY (PlanId) REFERENCES Plans(PlanId)
            );

            CREATE INDEX IF NOT EXISTS IX_PlanSteps_Plan_Index
                ON PlanSteps (PlanId, StepIndex);
            """;
        cmd.ExecuteNonQuery();
    }

    // ── Writes ───────────────────────────────────────────────────────────

    public async Task CreatePlanAsync(PlanWrite plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.PlanId);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.Subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.Request);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.Status);
        ArgumentNullException.ThrowIfNull(plan.Steps);

        string now = plan.CreatedAt.ToString(_iso8601, CultureInfo.InvariantCulture);
        string intents = plan.DetectedIntents is { Count: > 0 }
            ? string.Join(_intentSeparator, plan.DetectedIntents)
            : "";

        await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString, ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using (SqliteCommand insertPlan = conn.CreateCommand())
        {
            insertPlan.Transaction = tx;
            insertPlan.CommandText = """
                INSERT INTO Plans (PlanId, Subject, SessionId, TenantId, Request, DetectedIntents, Status, CreatedAt, UpdatedAt)
                VALUES (@pid, @subject, @sid, @tenant, @request, @intents, @status, @now, @now)
                """;
            insertPlan.Parameters.AddWithValue("@pid", plan.PlanId);
            insertPlan.Parameters.AddWithValue("@subject", plan.Subject);
            insertPlan.Parameters.AddWithValue("@sid", (object?)plan.SessionId ?? DBNull.Value);
            insertPlan.Parameters.AddWithValue("@tenant", (object?)plan.TenantId ?? DBNull.Value);
            insertPlan.Parameters.AddWithValue("@request", plan.Request);
            insertPlan.Parameters.AddWithValue("@intents", intents);
            insertPlan.Parameters.AddWithValue("@status", plan.Status);
            insertPlan.Parameters.AddWithValue("@now", now);
            await insertPlan.ExecuteNonQueryAsync(ct);
        }

        foreach (PlanStepWrite step in plan.Steps)
        {
            await using SqliteCommand insertStep = conn.CreateCommand();
            insertStep.Transaction = tx;
            insertStep.CommandText = """
                INSERT INTO PlanSteps (StepId, PlanId, StepIndex, SpecialistKey, Intent, Action, Status)
                VALUES (@sid, @pid, @idx, @key, @intent, @action, @status)
                """;
            insertStep.Parameters.AddWithValue("@sid", step.StepId);
            insertStep.Parameters.AddWithValue("@pid", plan.PlanId);
            insertStep.Parameters.AddWithValue("@idx", step.StepIndex);
            insertStep.Parameters.AddWithValue("@key", step.SpecialistKey);
            insertStep.Parameters.AddWithValue("@intent", step.Intent);
            insertStep.Parameters.AddWithValue("@action", step.Action);
            insertStep.Parameters.AddWithValue("@status", step.Status);
            await insertStep.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        _logger.LogDebug(
            "Persisted plan {PlanId} with {StepCount} steps for subject {Subject}",
            plan.PlanId, plan.Steps.Count, plan.Subject);
    }

    public async Task UpdatePlanStatusAsync(PlanStatusUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(update.PlanId);
        ArgumentException.ThrowIfNullOrWhiteSpace(update.Subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(update.Status);

        string now = update.UpdatedAt.ToString(_iso8601, CultureInfo.InvariantCulture);

        await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString, ct);
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE Plans SET
                Status = @status,
                FailureReason = COALESCE(@reason, FailureReason),
                TotalInputTokens = COALESCE(@in, TotalInputTokens),
                TotalOutputTokens = COALESCE(@out, TotalOutputTokens),
                TotalTokens = COALESCE(@tot, TotalTokens),
                TotalDurationMs = COALESCE(@dur, TotalDurationMs),
                UpdatedAt = @now
            WHERE PlanId = @pid AND Subject = @subject
            """;
        cmd.Parameters.AddWithValue("@status", update.Status);
        cmd.Parameters.AddWithValue("@reason", (object?)update.FailureReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@in", (object?)update.TotalInputTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@out", (object?)update.TotalOutputTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tot", (object?)update.TotalTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dur", (object?)update.TotalDurationMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@pid", update.PlanId);
        cmd.Parameters.AddWithValue("@subject", update.Subject);

        int rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows == 0)
        {
            _logger.LogWarning(
                "Plan status update rejected — no row for plan {PlanId} owned by subject {Subject}",
                update.PlanId, update.Subject);
        }
    }

    public async Task UpdateStepAsync(PlanStepUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(update.StepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(update.PlanId);
        ArgumentException.ThrowIfNullOrWhiteSpace(update.Subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(update.Status);

        await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString, ct);

        // Enforce ownership: the parent plan must belong to this subject. Doing
        // the guard as an EXISTS subquery keeps the UPDATE strictly write-only
        // and preserves the "cross-subject probe = 404" contract sessions use.
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE PlanSteps SET
                Status = @status,
                Result = COALESCE(@result, Result),
                Error = COALESCE(@error, Error),
                InputTokens = COALESCE(@in, InputTokens),
                OutputTokens = COALESCE(@out, OutputTokens),
                TotalTokens = COALESCE(@tot, TotalTokens),
                DurationMs = COALESCE(@dur, DurationMs),
                StartedAt = COALESCE(@started, StartedAt),
                CompletedAt = COALESCE(@completed, CompletedAt)
            WHERE StepId = @sid AND PlanId = @pid
              AND EXISTS (SELECT 1 FROM Plans p WHERE p.PlanId = @pid AND p.Subject = @subject)
            """;
        cmd.Parameters.AddWithValue("@sid", update.StepId);
        cmd.Parameters.AddWithValue("@pid", update.PlanId);
        cmd.Parameters.AddWithValue("@subject", update.Subject);
        cmd.Parameters.AddWithValue("@status", update.Status);
        cmd.Parameters.AddWithValue("@result", (object?)update.Result ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@error", (object?)update.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@in", (object?)update.InputTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@out", (object?)update.OutputTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tot", (object?)update.TotalTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dur", (object?)update.DurationMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@started",
            update.StartedAt is { } s ? s.ToString(_iso8601, CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("@completed",
            update.CompletedAt is { } c ? c.ToString(_iso8601, CultureInfo.InvariantCulture) : DBNull.Value);

        int rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows == 0)
        {
            _logger.LogWarning(
                "Step update rejected — no row for step {StepId} in plan {PlanId} owned by subject {Subject}",
                update.StepId, update.PlanId, update.Subject);
        }
    }

    // ── Reads ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PlanSummaryDto>> ListPlansForSubjectAsync(
        string subject, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString, ct);
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT p.PlanId, p.SessionId, p.TenantId, p.Request, p.Status,
                   (SELECT COUNT(*) FROM PlanSteps s WHERE s.PlanId = p.PlanId) AS StepCount,
                   p.CreatedAt, p.UpdatedAt
            FROM Plans p
            WHERE p.Subject = @subject
            ORDER BY p.UpdatedAt DESC
            LIMIT 500
            """;
        cmd.Parameters.AddWithValue("@subject", subject);

        var results = new List<PlanSummaryDto>();
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new PlanSummaryDto(
                PlanId: reader.GetString(0),
                SessionId: reader.IsDBNull(1) ? null : reader.GetString(1),
                TenantId: reader.IsDBNull(2) ? null : reader.GetString(2),
                Request: reader.GetString(3),
                Status: reader.GetString(4),
                StepCount: reader.GetInt32(5),
                CreatedAt: ParseTimestamp(reader.GetString(6)),
                UpdatedAt: ParseTimestamp(reader.GetString(7))));
        }
        return results;
    }

    public async Task<PlanDetailDto?> GetPlanAsync(
        string subject, string planId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);

        await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString, ct);

        PlanDetailDto? header = null;
        await using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT PlanId, SessionId, TenantId, Request, Status, DetectedIntents,
                       FailureReason, TotalInputTokens, TotalOutputTokens, TotalTokens,
                       TotalDurationMs, CreatedAt, UpdatedAt
                FROM Plans
                WHERE PlanId = @pid AND Subject = @subject
                """;
            cmd.Parameters.AddWithValue("@pid", planId);
            cmd.Parameters.AddWithValue("@subject", subject);

            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            string intentField = reader.GetString(5);
            IReadOnlyList<string> intents = string.IsNullOrEmpty(intentField)
                ? []
                : [.. intentField.Split(_intentSeparator, StringSplitOptions.RemoveEmptyEntries)];

            header = new PlanDetailDto(
                PlanId: reader.GetString(0),
                SessionId: reader.IsDBNull(1) ? null : reader.GetString(1),
                TenantId: reader.IsDBNull(2) ? null : reader.GetString(2),
                Request: reader.GetString(3),
                Status: reader.GetString(4),
                DetectedIntents: intents,
                FailureReason: reader.IsDBNull(6) ? null : reader.GetString(6),
                TotalInputTokens: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                TotalOutputTokens: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                TotalTokens: reader.IsDBNull(9) ? null : reader.GetInt32(9),
                TotalDurationMs: reader.IsDBNull(10) ? null : reader.GetInt64(10),
                CreatedAt: ParseTimestamp(reader.GetString(11)),
                UpdatedAt: ParseTimestamp(reader.GetString(12)),
                Steps: []);
        }

        var steps = new List<PlanStepRecordDto>();
        await using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT StepId, PlanId, StepIndex, SpecialistKey, Intent, Action, Status,
                       Result, Error, InputTokens, OutputTokens, TotalTokens, DurationMs,
                       StartedAt, CompletedAt
                FROM PlanSteps
                WHERE PlanId = @pid
                ORDER BY StepIndex ASC, rowid ASC
                """;
            cmd.Parameters.AddWithValue("@pid", planId);

            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                steps.Add(new PlanStepRecordDto(
                    StepId: reader.GetString(0),
                    PlanId: reader.GetString(1),
                    StepIndex: reader.GetInt32(2),
                    SpecialistKey: reader.GetString(3),
                    Intent: reader.GetString(4),
                    Action: reader.GetString(5),
                    Status: reader.GetString(6),
                    Result: reader.IsDBNull(7) ? null : reader.GetString(7),
                    Error: reader.IsDBNull(8) ? null : reader.GetString(8),
                    InputTokens: reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    OutputTokens: reader.IsDBNull(10) ? null : reader.GetInt32(10),
                    TotalTokens: reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    DurationMs: reader.IsDBNull(12) ? null : reader.GetInt64(12),
                    StartedAt: reader.IsDBNull(13) ? null : ParseTimestamp(reader.GetString(13)),
                    CompletedAt: reader.IsDBNull(14) ? null : ParseTimestamp(reader.GetString(14))));
            }
        }

        return header with { Steps = steps };
    }

    public async Task<bool> DeletePlanAsync(
        string subject, string planId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);

        await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString, ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using (SqliteCommand ownership = conn.CreateCommand())
        {
            ownership.Transaction = tx;
            ownership.CommandText = "SELECT 1 FROM Plans WHERE PlanId = @pid AND Subject = @subject";
            ownership.Parameters.AddWithValue("@pid", planId);
            ownership.Parameters.AddWithValue("@subject", subject);
            object? row = await ownership.ExecuteScalarAsync(ct);
            if (row is null)
            {
                await tx.RollbackAsync(ct);
                return false;
            }
        }

        await using (SqliteCommand deleteSteps = conn.CreateCommand())
        {
            deleteSteps.Transaction = tx;
            deleteSteps.CommandText = "DELETE FROM PlanSteps WHERE PlanId = @pid";
            deleteSteps.Parameters.AddWithValue("@pid", planId);
            await deleteSteps.ExecuteNonQueryAsync(ct);
        }

        await using (SqliteCommand deletePlan = conn.CreateCommand())
        {
            deletePlan.Transaction = tx;
            deletePlan.CommandText = "DELETE FROM Plans WHERE PlanId = @pid AND Subject = @subject";
            deletePlan.Parameters.AddWithValue("@pid", planId);
            deletePlan.Parameters.AddWithValue("@subject", subject);
            await deletePlan.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Deleted plan {PlanId} for subject {Subject}", planId, subject);
        return true;
    }

    public async Task<PlanCleanupResult> PurgeExpiredAsync(
        DateTimeOffset olderThan, CancellationToken ct = default)
    {
        string cutoff = olderThan.ToString(_iso8601, CultureInfo.InvariantCulture);

        await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString, ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        int stepRows;
        await using (SqliteCommand deleteSteps = conn.CreateCommand())
        {
            deleteSteps.Transaction = tx;
            deleteSteps.CommandText = """
                DELETE FROM PlanSteps
                WHERE PlanId IN (
                    SELECT PlanId FROM Plans WHERE UpdatedAt < @cutoff)
                """;
            deleteSteps.Parameters.AddWithValue("@cutoff", cutoff);
            stepRows = await deleteSteps.ExecuteNonQueryAsync(ct);
        }

        int planRows;
        await using (SqliteCommand deletePlans = conn.CreateCommand())
        {
            deletePlans.Transaction = tx;
            deletePlans.CommandText = "DELETE FROM Plans WHERE UpdatedAt < @cutoff";
            deletePlans.Parameters.AddWithValue("@cutoff", cutoff);
            planRows = await deletePlans.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new PlanCleanupResult(planRows, stepRows);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}

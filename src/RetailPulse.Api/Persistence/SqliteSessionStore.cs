using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetailPulse.Api.Data;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Persistence;

namespace RetailPulse.Api.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="ISessionStore"/>. Mirrors the shape of the
/// other durable stores (<see cref="Approval.SqliteApprovalGate"/>,
/// <see cref="Memory.SqliteConversationMemory"/>,
/// <see cref="Alerts.SqliteAlertService"/>): the shared cache connection
/// string is built once, every operation opens its own connection through
/// <see cref="SqliteMount"/> so it gets the SMB-safe pragmas
/// (<c>busy_timeout=10000</c>, <c>journal_mode=DELETE</c>, <c>synchronous=FULL</c>),
/// and the schema is initialized eagerly on construction so the first request path is a
/// straight INSERT.
///
/// Ownership is enforced at the SQL layer — every read/delete filters on the caller's
/// subject in the WHERE clause, so a session id owned by a different subject cannot be
/// coerced into returning content by any endpoint. Cross-subject reads resolve to
/// <c>null</c>; the endpoint layer surfaces that as a 404.
/// </summary>
public sealed class SqliteSessionStore : ISessionStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteSessionStore> _logger;

    private const string _iso8601 = "yyyy-MM-ddTHH:mm:ss.fffzzz";

    private static readonly JsonSerializerOptions _chartJson = new()
    {
        WriteIndented = false,
    };

    public SqliteSessionStore(string dbPath, ILogger<SqliteSessionStore> logger)
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

        _logger.LogInformation("Session store initialized at {DbPath}", dbPath);
    }

    // ── Schema ───────────────────────────────────────────────────────────

    private void InitializeSchema()
    {
        using SqliteConnection conn = SqliteMount.Open(_connectionString);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Sessions (
                SessionId       TEXT    PRIMARY KEY,
                Subject         TEXT    NOT NULL COLLATE NOCASE,
                TenantId        TEXT,
                CreatedAt       TEXT    NOT NULL,
                LastActivityAt  TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Sessions_Subject_LastActivity
                ON Sessions (Subject, LastActivityAt DESC);

            CREATE INDEX IF NOT EXISTS IX_Sessions_LastActivity
                ON Sessions (LastActivityAt);

            CREATE TABLE IF NOT EXISTS SessionTurns (
                TurnId              TEXT    PRIMARY KEY,
                SessionId           TEXT    NOT NULL,
                Role                TEXT    NOT NULL,
                Content             TEXT    NOT NULL,
                AgentId             TEXT,
                RoutingIntent       TEXT,
                RoutingAgentKey     TEXT,
                RoutingConfidence   REAL,
                InputTokens         INTEGER,
                OutputTokens        INTEGER,
                TotalTokens         INTEGER,
                ChartSpecsJson      TEXT,
                SpanSummary         TEXT,
                Timestamp           TEXT    NOT NULL,
                FOREIGN KEY (SessionId) REFERENCES Sessions(SessionId)
            );

            CREATE INDEX IF NOT EXISTS IX_SessionTurns_Session_Ts
                ON SessionTurns (SessionId, Timestamp);
            """;
        cmd.ExecuteNonQuery();
    }

    // ── Writes ───────────────────────────────────────────────────────────

    public async Task PersistTurnAsync(SessionTurnWrite write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentException.ThrowIfNullOrWhiteSpace(write.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(write.Subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(write.Role);

        string now = write.Timestamp.ToString(_iso8601, CultureInfo.InvariantCulture);
        string turnId = Guid.NewGuid().ToString("N");
        string? chartJson = write.Charts is { Count: > 0 }
            ? JsonSerializer.Serialize(write.Charts, _chartJson)
            : null;

        await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString, ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        // Upsert session — created-at survives, last-activity always advances. The
        // subject is fixed at the first write (INSERT OR IGNORE) so a caller cannot
        // overwrite an existing owner even if the same session id is replayed.
        await using (SqliteCommand insertSession = conn.CreateCommand())
        {
            insertSession.Transaction = tx;
            insertSession.CommandText = """
                INSERT INTO Sessions (SessionId, Subject, TenantId, CreatedAt, LastActivityAt)
                VALUES (@sid, @subject, @tenant, @now, @now)
                ON CONFLICT(SessionId) DO UPDATE SET LastActivityAt = excluded.LastActivityAt
                    WHERE Sessions.Subject = excluded.Subject
                """;
            insertSession.Parameters.AddWithValue("@sid", write.SessionId);
            insertSession.Parameters.AddWithValue("@subject", write.Subject);
            insertSession.Parameters.AddWithValue("@tenant", (object?)write.TenantId ?? DBNull.Value);
            insertSession.Parameters.AddWithValue("@now", now);
            await insertSession.ExecuteNonQueryAsync(ct);
        }

        // Guard against writing a turn to a session already owned by a different
        // subject. If the upsert above matched the existing subject the row is
        // present; otherwise the write is a no-op and we fail closed rather than
        // dropping an orphan turn.
        await using (SqliteCommand ownership = conn.CreateCommand())
        {
            ownership.Transaction = tx;
            ownership.CommandText = "SELECT Subject FROM Sessions WHERE SessionId = @sid";
            ownership.Parameters.AddWithValue("@sid", write.SessionId);
            object? owner = await ownership.ExecuteScalarAsync(ct);
            if (owner is not string ownerStr || !string.Equals(ownerStr, write.Subject, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(ct);
                _logger.LogWarning(
                    "Session {SessionId} is owned by a different subject — turn write rejected.",
                    write.SessionId);
                return;
            }
        }

        await using (SqliteCommand insertTurn = conn.CreateCommand())
        {
            insertTurn.Transaction = tx;
            insertTurn.CommandText = """
                INSERT INTO SessionTurns (
                    TurnId, SessionId, Role, Content, AgentId,
                    RoutingIntent, RoutingAgentKey, RoutingConfidence,
                    InputTokens, OutputTokens, TotalTokens,
                    ChartSpecsJson, SpanSummary, Timestamp)
                VALUES (
                    @tid, @sid, @role, @content, @agentId,
                    @intent, @agentKey, @confidence,
                    @inTokens, @outTokens, @totalTokens,
                    @chartsJson, @spanSummary, @ts)
                """;
            insertTurn.Parameters.AddWithValue("@tid", turnId);
            insertTurn.Parameters.AddWithValue("@sid", write.SessionId);
            insertTurn.Parameters.AddWithValue("@role", write.Role);
            insertTurn.Parameters.AddWithValue("@content", write.Content);
            insertTurn.Parameters.AddWithValue("@agentId", (object?)write.AgentId ?? DBNull.Value);
            insertTurn.Parameters.AddWithValue("@intent", (object?)write.RoutingIntent ?? DBNull.Value);
            insertTurn.Parameters.AddWithValue("@agentKey", (object?)write.RoutingAgentKey ?? DBNull.Value);
            insertTurn.Parameters.AddWithValue("@confidence", (object?)write.RoutingConfidence ?? DBNull.Value);
            insertTurn.Parameters.AddWithValue("@inTokens", (object?)write.InputTokens ?? DBNull.Value);
            insertTurn.Parameters.AddWithValue("@outTokens", (object?)write.OutputTokens ?? DBNull.Value);
            insertTurn.Parameters.AddWithValue("@totalTokens", (object?)write.TotalTokens ?? DBNull.Value);
            insertTurn.Parameters.AddWithValue("@chartsJson", (object?)chartJson ?? DBNull.Value);
            insertTurn.Parameters.AddWithValue("@spanSummary", (object?)write.SpanSummary ?? DBNull.Value);
            insertTurn.Parameters.AddWithValue("@ts", now);
            await insertTurn.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        _logger.LogDebug(
            "Persisted {Role} turn {TurnId} to session {SessionId} for subject {Subject}",
            write.Role, turnId, write.SessionId, write.Subject);
    }

    // ── Reads ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SessionSummaryDto>> ListSessionsForSubjectAsync(
        string subject, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString, ct);
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.SessionId, s.TenantId, s.CreatedAt, s.LastActivityAt,
                   (SELECT COUNT(*) FROM SessionTurns t WHERE t.SessionId = s.SessionId) AS TurnCount
            FROM Sessions s
            WHERE s.Subject = @subject
            ORDER BY s.LastActivityAt DESC
            LIMIT 500
            """;
        cmd.Parameters.AddWithValue("@subject", subject);

        var results = new List<SessionSummaryDto>();
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new SessionSummaryDto(
                SessionId: reader.GetString(0),
                TenantId: reader.IsDBNull(1) ? null : reader.GetString(1),
                CreatedAt: ParseTimestamp(reader.GetString(2)),
                LastActivityAt: ParseTimestamp(reader.GetString(3)),
                TurnCount: reader.GetInt32(4)));
        }
        return results;
    }

    public async Task<SessionDetailDto?> GetSessionAsync(
        string subject, string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString, ct);

        SessionDetailDto? header = null;
        await using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT SessionId, TenantId, CreatedAt, LastActivityAt
                FROM Sessions
                WHERE SessionId = @sid AND Subject = @subject
                """;
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue("@subject", subject);

            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            header = new SessionDetailDto(
                SessionId: reader.GetString(0),
                TenantId: reader.IsDBNull(1) ? null : reader.GetString(1),
                CreatedAt: ParseTimestamp(reader.GetString(2)),
                LastActivityAt: ParseTimestamp(reader.GetString(3)),
                Turns: []);
        }

        var turns = new List<SessionTurnDto>();
        await using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT TurnId, Role, Content, AgentId,
                       RoutingIntent, RoutingAgentKey, RoutingConfidence,
                       InputTokens, OutputTokens, TotalTokens,
                       ChartSpecsJson, SpanSummary, Timestamp
                FROM SessionTurns
                WHERE SessionId = @sid
                ORDER BY Timestamp ASC, TurnId ASC
                """;
            cmd.Parameters.AddWithValue("@sid", sessionId);

            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                turns.Add(new SessionTurnDto(
                    TurnId: reader.GetString(0),
                    Role: reader.GetString(1),
                    Content: reader.GetString(2),
                    AgentId: reader.IsDBNull(3) ? null : reader.GetString(3),
                    RoutingIntent: reader.IsDBNull(4) ? null : reader.GetString(4),
                    RoutingAgentKey: reader.IsDBNull(5) ? null : reader.GetString(5),
                    RoutingConfidence: reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    InputTokens: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    OutputTokens: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    TotalTokens: reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    Charts: reader.IsDBNull(10) ? null : DeserializeCharts(reader.GetString(10)),
                    SpanSummary: reader.IsDBNull(11) ? null : reader.GetString(11),
                    Timestamp: ParseTimestamp(reader.GetString(12))));
            }
        }

        return header with { Turns = turns };
    }

    public async Task<bool> DeleteSessionAsync(
        string subject, string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString, ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        // Verify ownership up front. Doing the check as a SELECT (rather than a
        // conditional DELETE ... RETURNING) lets us short-circuit before touching
        // either table, and it means the FK constraint between SessionTurns and
        // Sessions never has to be reasoned about with a half-applied delete.
        await using (SqliteCommand ownership = conn.CreateCommand())
        {
            ownership.Transaction = tx;
            ownership.CommandText = "SELECT 1 FROM Sessions WHERE SessionId = @sid AND Subject = @subject";
            ownership.Parameters.AddWithValue("@sid", sessionId);
            ownership.Parameters.AddWithValue("@subject", subject);
            object? row = await ownership.ExecuteScalarAsync(ct);
            if (row is null)
            {
                await tx.RollbackAsync(ct);
                return false;
            }
        }

        // Delete order matters: SessionTurns.SessionId has a FOREIGN KEY reference
        // to Sessions.SessionId, so the child rows must go first (Microsoft.Data.Sqlite
        // enables foreign_keys=ON per connection by default). Same transaction, so
        // an interrupted purge cannot leave orphan turns behind.
        await using (SqliteCommand deleteTurns = conn.CreateCommand())
        {
            deleteTurns.Transaction = tx;
            deleteTurns.CommandText = "DELETE FROM SessionTurns WHERE SessionId = @sid";
            deleteTurns.Parameters.AddWithValue("@sid", sessionId);
            await deleteTurns.ExecuteNonQueryAsync(ct);
        }

        await using (SqliteCommand deleteSession = conn.CreateCommand())
        {
            deleteSession.Transaction = tx;
            deleteSession.CommandText = """
                DELETE FROM Sessions
                WHERE SessionId = @sid AND Subject = @subject
                """;
            deleteSession.Parameters.AddWithValue("@sid", sessionId);
            deleteSession.Parameters.AddWithValue("@subject", subject);
            await deleteSession.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Deleted session {SessionId} for subject {Subject}", sessionId, subject);
        return true;
    }

    public async Task<CleanupResult> PurgeExpiredAsync(
        DateTimeOffset olderThan, CancellationToken ct = default)
    {
        string cutoff = olderThan.ToString(_iso8601, CultureInfo.InvariantCulture);

        await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString, ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        int turnRows;
        await using (SqliteCommand deleteTurns = conn.CreateCommand())
        {
            deleteTurns.Transaction = tx;
            deleteTurns.CommandText = """
                DELETE FROM SessionTurns
                WHERE SessionId IN (
                    SELECT SessionId FROM Sessions WHERE LastActivityAt < @cutoff)
                """;
            deleteTurns.Parameters.AddWithValue("@cutoff", cutoff);
            turnRows = await deleteTurns.ExecuteNonQueryAsync(ct);
        }

        int sessionRows;
        await using (SqliteCommand deleteSessions = conn.CreateCommand())
        {
            deleteSessions.Transaction = tx;
            deleteSessions.CommandText = "DELETE FROM Sessions WHERE LastActivityAt < @cutoff";
            deleteSessions.Parameters.AddWithValue("@cutoff", cutoff);
            sessionRows = await deleteSessions.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new CleanupResult(sessionRows, turnRows);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static IReadOnlyList<ChartSpec>? DeserializeCharts(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ChartSpec>>(json, _chartJson);
        }
        catch (JsonException)
        {
            // Malformed charts should never break rehydrate — surface no charts and
            // let the caller keep the rest of the transcript.
            return null;
        }
    }
}

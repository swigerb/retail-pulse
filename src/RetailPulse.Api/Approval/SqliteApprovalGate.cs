using System.Globalization;
using Microsoft.Data.Sqlite;
using RetailPulse.Contracts.Approval;

namespace RetailPulse.Api.Approval;

/// <summary>
/// SQLite-backed implementation of <see cref="IApprovalGate"/>.
/// Thread-safe — each operation opens its own connection from the shared WAL database.
/// The approval table is append-only for audit compliance; decisions are updated in-place
/// but original creation records are immutable.
/// </summary>
public sealed class SqliteApprovalGate : IApprovalGate
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteApprovalGate> _logger;
    private readonly TimeSpan _defaultTimeout;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const string Iso8601 = "yyyy-MM-ddTHH:mm:ss.fffzzz";

    public SqliteApprovalGate(string dbPath, ILogger<SqliteApprovalGate> logger, TimeSpan? defaultTimeout = null)
    {
        _logger = logger;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromMinutes(5);

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS ApprovalRequests (
                RequestId   TEXT PRIMARY KEY,
                AgentId     TEXT NOT NULL,
                UserId      TEXT NOT NULL,
                Action      TEXT NOT NULL,
                Impact      TEXT,
                Urgency     TEXT DEFAULT 'medium',
                Reasoning   TEXT,
                Decision    TEXT DEFAULT 'Pending',
                Comment     TEXT,
                CreatedAt   TEXT NOT NULL,
                ExpiresAt   TEXT NOT NULL,
                RespondedAt TEXT
            );

            CREATE INDEX IF NOT EXISTS IX_ApprovalRequests_UserId_Decision
                ON ApprovalRequests (UserId, Decision);

            CREATE INDEX IF NOT EXISTS IX_ApprovalRequests_CreatedAt
                ON ApprovalRequests (CreatedAt DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    public Task<ApprovalRequest> RequestApprovalAsync(ApprovalContext context, CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(_defaultTimeout);

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ApprovalRequests (RequestId, AgentId, UserId, Action, Impact, Urgency, Reasoning, Decision, CreatedAt, ExpiresAt)
            VALUES (@id, @agentId, @userId, @action, @impact, @urgency, @reasoning, 'Pending', @createdAt, @expiresAt)
            """;
        cmd.Parameters.AddWithValue("@id", requestId);
        cmd.Parameters.AddWithValue("@agentId", context.AgentId);
        cmd.Parameters.AddWithValue("@userId", context.UserId);
        cmd.Parameters.AddWithValue("@action", context.Action);
        cmd.Parameters.AddWithValue("@impact", (object?)context.Impact ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@urgency", context.Urgency);
        cmd.Parameters.AddWithValue("@reasoning", (object?)context.Reasoning ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", now.ToString(Iso8601));
        cmd.Parameters.AddWithValue("@expiresAt", expiresAt.ToString(Iso8601));
        cmd.ExecuteNonQuery();

        _logger.LogInformation(
            "Approval request {RequestId} created for agent {AgentId}, user {UserId}, urgency {Urgency}",
            requestId, context.AgentId, context.UserId, context.Urgency);

        var request = new ApprovalRequest(requestId, context, now, expiresAt);
        return Task.FromResult(request);
    }

    public Task<ApprovalResult> GetResultAsync(string requestId, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        var row = ReadRow(conn, requestId)
            ?? throw new KeyNotFoundException($"Approval request '{requestId}' not found.");

        return Task.FromResult(new ApprovalResult(
            row.RequestId,
            row.Decision,
            row.Comment,
            row.RespondedAt));
    }

    public async Task<ApprovalResult> WaitForApprovalAsync(string requestId, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var effectiveTimeout = timeout ?? _defaultTimeout;
        var deadline = DateTimeOffset.UtcNow.Add(effectiveTimeout);

        _logger.LogInformation(
            "Waiting for approval {RequestId} with timeout {Timeout}",
            requestId, effectiveTimeout);

        while (!ct.IsCancellationRequested)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            var row = ReadRow(conn, requestId)
                ?? throw new KeyNotFoundException($"Approval request '{requestId}' not found.");

            if (row.Decision != ApprovalDecision.Pending)
            {
                return new ApprovalResult(row.RequestId, row.Decision, row.Comment, row.RespondedAt);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                // Auto-timeout
                await RespondAsync(requestId, ApprovalDecision.TimedOut, "Approval timed out — no response received.", ct);
                return new ApprovalResult(requestId, ApprovalDecision.TimedOut, "Approval timed out — no response received.", DateTimeOffset.UtcNow);
            }

            await Task.Delay(PollInterval, ct);
        }

        ct.ThrowIfCancellationRequested();
        throw new OperationCanceledException(ct);
    }

    public Task RespondAsync(string requestId, ApprovalDecision decision, string? comment = null, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // Only allow responding to Pending requests (idempotent guard)
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE ApprovalRequests
            SET Decision = @decision, Comment = @comment, RespondedAt = @respondedAt
            WHERE RequestId = @id AND Decision = 'Pending'
            """;
        cmd.Parameters.AddWithValue("@id", requestId);
        cmd.Parameters.AddWithValue("@decision", decision.ToString());
        cmd.Parameters.AddWithValue("@comment", (object?)comment ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@respondedAt", now.ToString(Iso8601));

        var affected = cmd.ExecuteNonQuery();
        if (affected == 0)
        {
            _logger.LogWarning("Approval {RequestId} was not updated — it may already be resolved.", requestId);
        }
        else
        {
            _logger.LogInformation(
                "Approval {RequestId} resolved as {Decision} at {RespondedAt}",
                requestId, decision, now);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ApprovalRequest>> GetPendingAsync(string userId, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM ApprovalRequests
            WHERE UserId = @userId AND Decision = 'Pending'
            ORDER BY CreatedAt DESC
            """;
        cmd.Parameters.AddWithValue("@userId", userId);

        var results = ReadAll(cmd);
        return Task.FromResult<IReadOnlyList<ApprovalRequest>>(results);
    }

    public Task<IReadOnlyList<ApprovalRequest>> GetHistoryAsync(int limit = 50, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM ApprovalRequests
            WHERE Decision != 'Pending'
            ORDER BY RespondedAt DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = ReadAll(cmd);
        return Task.FromResult<IReadOnlyList<ApprovalRequest>>(results);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static ApprovalRequest? ReadRow(SqliteConnection conn, string requestId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM ApprovalRequests WHERE RequestId = @id";
        cmd.Parameters.AddWithValue("@id", requestId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return MapRow(reader);
    }

    private static List<ApprovalRequest> ReadAll(SqliteCommand cmd)
    {
        var results = new List<ApprovalRequest>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(MapRow(reader));
        }
        return results;
    }

    private static ApprovalRequest MapRow(SqliteDataReader reader)
    {
        var requestId = reader.GetString(reader.GetOrdinal("RequestId"));
        var context = new ApprovalContext(
            AgentId: reader.GetString(reader.GetOrdinal("AgentId")),
            UserId: reader.GetString(reader.GetOrdinal("UserId")),
            Action: reader.GetString(reader.GetOrdinal("Action")),
            Impact: reader.IsDBNull(reader.GetOrdinal("Impact")) ? "" : reader.GetString(reader.GetOrdinal("Impact")),
            Urgency: reader.IsDBNull(reader.GetOrdinal("Urgency")) ? "medium" : reader.GetString(reader.GetOrdinal("Urgency")),
            Reasoning: reader.IsDBNull(reader.GetOrdinal("Reasoning")) ? "" : reader.GetString(reader.GetOrdinal("Reasoning"))
        );

        var decisionStr = reader.GetString(reader.GetOrdinal("Decision"));
        var decision = Enum.TryParse<ApprovalDecision>(decisionStr, true, out var d) ? d : ApprovalDecision.Pending;

        var comment = reader.IsDBNull(reader.GetOrdinal("Comment")) ? null : reader.GetString(reader.GetOrdinal("Comment"));
        var createdAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("CreatedAt")), CultureInfo.InvariantCulture);
        var expiresAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("ExpiresAt")), CultureInfo.InvariantCulture);
        var respondedAt = reader.IsDBNull(reader.GetOrdinal("RespondedAt"))
            ? (DateTimeOffset?)null
            : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("RespondedAt")), CultureInfo.InvariantCulture);

        return new ApprovalRequest(requestId, context, createdAt, expiresAt, decision, comment, respondedAt);
    }
}

using System.Globalization;
using Microsoft.Data.Sqlite;
using RetailPulse.Contracts.Approval;

namespace RetailPulse.Api.Approval;

/// <summary>
/// SQLite-backed implementation of <see cref="IApprovalGate"/>.
/// Thread-safe — each operation opens its own connection from the shared WAL database.
/// All database I/O uses async APIs with CancellationToken support.
/// WaitForApprovalAsync uses exponential backoff instead of fixed-interval polling.
/// </summary>
public sealed class SqliteApprovalGate : IApprovalGate
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteApprovalGate> _logger;
    private readonly TimeSpan _defaultTimeout;

    private const string _iso8601 = "yyyy-MM-ddTHH:mm:ss.fffzzz";

    // Exponential backoff parameters for WaitForApprovalAsync
    internal static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(4);
    internal const double BackoffMultiplier = 2.0;

    public SqliteApprovalGate(string dbPath, ILogger<SqliteApprovalGate> logger, TimeSpan? defaultTimeout = null)
    {
        _logger = logger;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromMinutes(5);

        string? dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        InitializeSchemaAsync().GetAwaiter().GetResult();
    }

    private async Task InitializeSchemaAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using SqliteCommand cmd = conn.CreateCommand();
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
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<ApprovalRequest> RequestApprovalAsync(ApprovalContext context, CancellationToken ct = default)
    {
        string requestId = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset expiresAt = now.Add(_defaultTimeout);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using SqliteCommand cmd = conn.CreateCommand();
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
        cmd.Parameters.AddWithValue("@createdAt", now.ToString(_iso8601, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@expiresAt", expiresAt.ToString(_iso8601, CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation(
            "Approval request {RequestId} created for agent {AgentId}, user {UserId}, urgency {Urgency}",
            requestId, context.AgentId, context.UserId, context.Urgency);

        return new ApprovalRequest(requestId, context, now, expiresAt);
    }

    public async Task<ApprovalResult> GetResultAsync(string requestId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        ApprovalRequest row = await ReadRowAsync(conn, requestId, ct)
            ?? throw new KeyNotFoundException($"Approval request '{requestId}' not found.");

        return new ApprovalResult(
            row.RequestId,
            row.Decision,
            row.Comment,
            row.RespondedAt);
    }

    public async Task<ApprovalResult> WaitForApprovalAsync(string requestId, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        TimeSpan effectiveTimeout = timeout ?? _defaultTimeout;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(effectiveTimeout);
        TimeSpan backoff = InitialBackoff;

        _logger.LogInformation(
            "Waiting for approval {RequestId} with timeout {Timeout}",
            requestId, effectiveTimeout);

        while (!ct.IsCancellationRequested)
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            ApprovalRequest row = await ReadRowAsync(conn, requestId, ct)
                ?? throw new KeyNotFoundException($"Approval request '{requestId}' not found.");

            if (row.Decision != ApprovalDecision.Pending)
            {
                return new ApprovalResult(row.RequestId, row.Decision, row.Comment, row.RespondedAt);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                await RespondAsync(requestId, ApprovalDecision.TimedOut, "Approval timed out — no response received.", ct);
                return new ApprovalResult(requestId, ApprovalDecision.TimedOut, "Approval timed out — no response received.", DateTimeOffset.UtcNow);
            }

            await Task.Delay(backoff, ct);
            backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * BackoffMultiplier, MaxBackoff.TotalMilliseconds));
        }

        ct.ThrowIfCancellationRequested();
        throw new OperationCanceledException(ct);
    }

    public async Task RespondAsync(string requestId, ApprovalDecision decision, string? comment = null, CancellationToken ct = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE ApprovalRequests
            SET Decision = @decision, Comment = @comment, RespondedAt = @respondedAt
            WHERE RequestId = @id AND Decision = 'Pending'
            """;
        cmd.Parameters.AddWithValue("@id", requestId);
        cmd.Parameters.AddWithValue("@decision", decision.ToString());
        cmd.Parameters.AddWithValue("@comment", (object?)comment ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@respondedAt", now.ToString(_iso8601, CultureInfo.InvariantCulture));

        int affected = await cmd.ExecuteNonQueryAsync(ct);
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
    }

    public async Task<IReadOnlyList<ApprovalRequest>> GetPendingAsync(string userId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM ApprovalRequests
            WHERE UserId = @userId AND Decision = 'Pending'
            ORDER BY CreatedAt DESC
            """;
        cmd.Parameters.AddWithValue("@userId", userId);

        return await ReadAllAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<ApprovalRequest>> GetHistoryAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM ApprovalRequests
            WHERE Decision != 'Pending'
            ORDER BY RespondedAt DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        return await ReadAllAsync(cmd, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static async Task<ApprovalRequest?> ReadRowAsync(SqliteConnection conn, string requestId, CancellationToken ct = default)
    {
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM ApprovalRequests WHERE RequestId = @id";
        cmd.Parameters.AddWithValue("@id", requestId);

        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
        return !await reader.ReadAsync(ct) ? null : MapRow(reader);
    }

    private static async Task<List<ApprovalRequest>> ReadAllAsync(SqliteCommand cmd, CancellationToken ct = default)
    {
        var results = new List<ApprovalRequest>();
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapRow(reader));
        }
        return results;
    }

    private static ApprovalRequest MapRow(SqliteDataReader reader)
    {
        string requestId = reader.GetString(reader.GetOrdinal("RequestId"));
        var context = new ApprovalContext(
            AgentId: reader.GetString(reader.GetOrdinal("AgentId")),
            UserId: reader.GetString(reader.GetOrdinal("UserId")),
            Action: reader.GetString(reader.GetOrdinal("Action")),
            Impact: reader.IsDBNull(reader.GetOrdinal("Impact")) ? "" : reader.GetString(reader.GetOrdinal("Impact")),
            Urgency: reader.IsDBNull(reader.GetOrdinal("Urgency")) ? "medium" : reader.GetString(reader.GetOrdinal("Urgency")),
            Reasoning: reader.IsDBNull(reader.GetOrdinal("Reasoning")) ? "" : reader.GetString(reader.GetOrdinal("Reasoning"))
        );

        string decisionStr = reader.GetString(reader.GetOrdinal("Decision"));
        ApprovalDecision decision = Enum.TryParse(decisionStr, true, out ApprovalDecision d) ? d : ApprovalDecision.Pending;

        string? comment = reader.IsDBNull(reader.GetOrdinal("Comment")) ? null : reader.GetString(reader.GetOrdinal("Comment"));
        var createdAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("CreatedAt")), CultureInfo.InvariantCulture);
        var expiresAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("ExpiresAt")), CultureInfo.InvariantCulture);
        DateTimeOffset? respondedAt = reader.IsDBNull(reader.GetOrdinal("RespondedAt"))
            ? null
            : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("RespondedAt")), CultureInfo.InvariantCulture);

        return new ApprovalRequest(requestId, context, createdAt, expiresAt, decision, comment, respondedAt);
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Api.Security;

/// <summary>
/// Append-only SQLite-backed audit log with tamper-detection via hash chain.
/// Each entry's checksum = SHA256(previous_checksum + current_entry_json).
/// </summary>
public class DurableAuditLog : IAuditLog, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private string _lastChecksum = string.Empty;

    public DurableAuditLog(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        InitializeSchema();
        LoadLastChecksum();
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS audit_log (
                id TEXT PRIMARY KEY,
                timestamp TEXT NOT NULL,
                user_id TEXT NOT NULL,
                agent_id TEXT NOT NULL,
                action TEXT NOT NULL,
                input_summary TEXT NOT NULL,
                output_summary TEXT NOT NULL,
                tokens_used INTEGER NOT NULL,
                duration_ms REAL NOT NULL,
                checksum TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_audit_timestamp ON audit_log(timestamp);
            CREATE INDEX IF NOT EXISTS idx_audit_user ON audit_log(user_id);
            CREATE INDEX IF NOT EXISTS idx_audit_agent ON audit_log(agent_id);
            CREATE INDEX IF NOT EXISTS idx_audit_action ON audit_log(action);
            """;
        cmd.ExecuteNonQuery();
    }

    private void LoadLastChecksum()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT checksum FROM audit_log ORDER BY rowid DESC LIMIT 1";
        var result = cmd.ExecuteScalar();
        _lastChecksum = result as string ?? string.Empty;
    }

    public async Task LogAsync(AuditEntry entry, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var timestampStr = entry.Timestamp.ToString("O", CultureInfo.InvariantCulture);
            var durationMs = entry.Duration.TotalMilliseconds;
            var entryJson = BuildChecksumPayload(entry.Id, timestampStr, entry.UserId, entry.AgentId,
                entry.Action, entry.InputSummary, entry.OutputSummary, entry.TokensUsed, durationMs);

            var checksum = ComputeChecksum(_lastChecksum, entryJson);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO audit_log (id, timestamp, user_id, agent_id, action, input_summary, output_summary, tokens_used, duration_ms, checksum)
                VALUES (@id, @ts, @uid, @aid, @action, @input, @output, @tokens, @duration, @checksum)
                """;
            cmd.Parameters.AddWithValue("@id", entry.Id);
            cmd.Parameters.AddWithValue("@ts", timestampStr);
            cmd.Parameters.AddWithValue("@uid", entry.UserId);
            cmd.Parameters.AddWithValue("@aid", entry.AgentId);
            cmd.Parameters.AddWithValue("@action", entry.Action);
            cmd.Parameters.AddWithValue("@input", entry.InputSummary);
            cmd.Parameters.AddWithValue("@output", entry.OutputSummary);
            cmd.Parameters.AddWithValue("@tokens", entry.TokensUsed);
            cmd.Parameters.AddWithValue("@duration", durationMs);
            cmd.Parameters.AddWithValue("@checksum", checksum);

            await Task.Run(() => cmd.ExecuteNonQuery(), ct);
            _lastChecksum = checksum;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken ct = default)
    {
        var sb = new StringBuilder("SELECT id, timestamp, user_id, agent_id, action, input_summary, output_summary, tokens_used, duration_ms FROM audit_log WHERE 1=1");
        var parameters = new List<SqliteParameter>();

        if (query.AgentId is not null)
        {
            sb.Append(" AND agent_id = @agentId");
            parameters.Add(new SqliteParameter("@agentId", query.AgentId));
        }
        if (query.UserId is not null)
        {
            sb.Append(" AND user_id = @userId");
            parameters.Add(new SqliteParameter("@userId", query.UserId));
        }
        if (query.From.HasValue)
        {
            sb.Append(" AND timestamp >= @from");
            parameters.Add(new SqliteParameter("@from", query.From.Value.ToString("O", CultureInfo.InvariantCulture)));
        }
        if (query.To.HasValue)
        {
            sb.Append(" AND timestamp <= @to");
            parameters.Add(new SqliteParameter("@to", query.To.Value.ToString("O", CultureInfo.InvariantCulture)));
        }
        if (query.Action is not null)
        {
            sb.Append(" AND action = @action");
            parameters.Add(new SqliteParameter("@action", query.Action));
        }

        sb.Append(" ORDER BY timestamp DESC LIMIT @limit");
        parameters.Add(new SqliteParameter("@limit", query.Limit));

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sb.ToString();
        foreach (var p in parameters)
            cmd.Parameters.Add(p);

        var results = new List<AuditEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new AuditEntry(
                reader.GetString(0),
                DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt32(7),
                TimeSpan.FromMilliseconds(reader.GetDouble(8))));
        }

        return Task.FromResult<IReadOnlyList<AuditEntry>>(results);
    }

    public Task<AuditStats> GetStatsAsync(CancellationToken ct = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audit_log";
        var total = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);

        var byAgent = new Dictionary<string, int>();
        using (var agentCmd = _connection.CreateCommand())
        {
            agentCmd.CommandText = "SELECT agent_id, COUNT(*) FROM audit_log GROUP BY agent_id";
            using var reader = agentCmd.ExecuteReader();
            while (reader.Read())
                byAgent[reader.GetString(0)] = reader.GetInt32(1);
        }

        var byAction = new Dictionary<string, int>();
        using (var actionCmd = _connection.CreateCommand())
        {
            actionCmd.CommandText = "SELECT action, COUNT(*) FROM audit_log GROUP BY action";
            using var reader = actionCmd.ExecuteReader();
            while (reader.Read())
                byAction[reader.GetString(0)] = reader.GetInt32(1);
        }

        return Task.FromResult(new AuditStats(total, byAgent, byAction));
    }

    /// <summary>
    /// Verifies the integrity of the entire audit chain.
    /// Returns true if no tampering detected, false otherwise.
    /// </summary>
    public bool VerifyIntegrity()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, timestamp, user_id, agent_id, action, input_summary, output_summary, tokens_used, duration_ms, checksum FROM audit_log ORDER BY rowid ASC";

        using var reader = cmd.ExecuteReader();
        var previousChecksum = string.Empty;

        while (reader.Read())
        {
            var entryJson = BuildChecksumPayload(
                reader.GetString(0),  // id
                reader.GetString(1),  // timestamp (stored as ISO string)
                reader.GetString(2),  // user_id
                reader.GetString(3),  // agent_id
                reader.GetString(4),  // action
                reader.GetString(5),  // input_summary
                reader.GetString(6),  // output_summary
                reader.GetInt32(7),   // tokens_used
                reader.GetDouble(8)); // duration_ms

            var expectedChecksum = ComputeChecksum(previousChecksum, entryJson);
            var actualChecksum = reader.GetString(9);

            if (expectedChecksum != actualChecksum)
                return false;

            previousChecksum = actualChecksum;
        }

        return true;
    }

    /// <summary>
    /// Builds a deterministic string payload for checksum computation.
    /// Uses pipe-delimited concatenation to avoid JSON serialization inconsistencies.
    /// </summary>
    private static string BuildChecksumPayload(string id, string timestamp, string userId, string agentId,
        string action, string inputSummary, string outputSummary, int tokensUsed, double durationMs)
    {
        return $"{id}|{timestamp}|{userId}|{agentId}|{action}|{inputSummary}|{outputSummary}|{tokensUsed}|{durationMs}";
    }

    internal static string ComputeChecksum(string previousChecksum, string entryJson)
    {
        var input = previousChecksum + entryJson;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash);
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}

using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace RetailPulse.Api.Resilience;

/// <summary>
/// In-memory dead-letter queue backed by a SQLite file for durability.
/// Stores failed operations for later replay.
/// </summary>
public sealed class DeadLetterQueue : IDisposable
{
    private readonly Channel<DeadLetterEntry> _channel;
    private readonly string _dbPath;
    private readonly ILogger<DeadLetterQueue> _logger;
    private readonly SqliteConnection _db;

    public DeadLetterQueue(ILogger<DeadLetterQueue> logger, string? dbPath = null)
    {
        _logger = logger;
        _dbPath = dbPath ?? Path.Combine(AppContext.BaseDirectory, "dead-letter.db");

        _channel = Channel.CreateBounded<DeadLetterEntry>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _db = new SqliteConnection($"Data Source={_dbPath}");
        _db.Open();
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS dead_letters (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                operation TEXT NOT NULL,
                payload TEXT,
                error TEXT NOT NULL,
                retry_count INTEGER NOT NULL DEFAULT 0,
                status TEXT NOT NULL DEFAULT 'pending'
            )
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task EnqueueAsync(string operation, string? payload, string error, CancellationToken ct = default)
    {
        var entry = new DeadLetterEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Operation = operation,
            Payload = payload,
            Error = error,
            RetryCount = 0
        };

        // Try in-memory channel first
        if (_channel.Writer.TryWrite(entry))
        {
            _logger.LogWarning("Dead-letter enqueued (in-memory): {Operation}", operation);
        }

        // Always persist to SQLite for durability
        await PersistAsync(entry, ct);
    }

    private async Task PersistAsync(DeadLetterEntry entry, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dead_letters (timestamp, operation, payload, error, retry_count, status)
            VALUES (@ts, @op, @payload, @err, @retry, 'pending')
            """;
        cmd.Parameters.AddWithValue("@ts", entry.Timestamp.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@op", entry.Operation);
        cmd.Parameters.AddWithValue("@payload", (object?)entry.Payload ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@err", entry.Error);
        cmd.Parameters.AddWithValue("@retry", entry.RetryCount);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<DeadLetterEntry>> GetPendingAsync(int limit = 50, CancellationToken ct = default)
    {
        var entries = new List<DeadLetterEntry>();
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, timestamp, operation, payload, error, retry_count FROM dead_letters WHERE status = 'pending' ORDER BY id LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            entries.Add(new DeadLetterEntry
            {
                Id = reader.GetInt64(0),
                Timestamp = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                Operation = reader.GetString(2),
                Payload = reader.IsDBNull(3) ? null : reader.GetString(3),
                Error = reader.GetString(4),
                RetryCount = reader.GetInt32(5)
            });
        }

        return entries;
    }

    public async Task MarkReplayedAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE dead_letters SET status = 'replayed' WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkFailedAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE dead_letters SET retry_count = retry_count + 1 WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> GetPendingCountAsync(CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dead_letters WHERE status = 'pending'";
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    /// <summary>Drain in-memory items (used for testing/diagnostics).</summary>
    public bool TryRead(out DeadLetterEntry? entry) => _channel.Reader.TryRead(out entry);

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _db.Dispose();
    }
}

public class DeadLetterEntry
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public required string Operation { get; set; }
    public string? Payload { get; set; }
    public required string Error { get; set; }
    public int RetryCount { get; set; }
}

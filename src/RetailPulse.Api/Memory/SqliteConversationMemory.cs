using Microsoft.Data.Sqlite;
using RetailPulse.Contracts.Memory;

namespace RetailPulse.Api.Memory;

/// <summary>
/// SQLite-backed implementation of <see cref="IConversationMemory"/>.
/// Uses WAL mode and connection pooling for thread-safety across
/// concurrent users. Expired entries are cleaned up lazily on recall.
/// </summary>
public sealed class SqliteConversationMemory : IConversationMemory, IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteConversationMemory> _logger;

    public SqliteConversationMemory(string dbPath, ILogger<SqliteConversationMemory> logger)
    {
        _logger = logger;

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

    // ── Schema ───────────────────────────────────────────────────────────

    private void InitializeSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS ConversationMemories (
                Id          TEXT    NOT NULL,
                UserId      TEXT    NOT NULL COLLATE NOCASE,
                Type        INTEGER NOT NULL,
                Content     TEXT    NOT NULL,
                EntityKey   TEXT    COLLATE NOCASE,
                CreatedAt   TEXT    NOT NULL,
                ExpiresAt   TEXT    NOT NULL,
                Relevance   REAL    NOT NULL DEFAULT 1.0,
                PRIMARY KEY (UserId, Id)
            );

            CREATE INDEX IF NOT EXISTS IX_ConversationMemories_UserId
                ON ConversationMemories (UserId);

            CREATE INDEX IF NOT EXISTS IX_ConversationMemories_ExpiresAt
                ON ConversationMemories (ExpiresAt);

            CREATE INDEX IF NOT EXISTS IX_ConversationMemories_EntityKey
                ON ConversationMemories (EntityKey)
                WHERE EntityKey IS NOT NULL;
            """;
        cmd.ExecuteNonQuery();

        _logger.LogInformation("ConversationMemories table initialized at {DbPath}",
            new SqliteConnectionStringBuilder(_connectionString).DataSource);
    }

    // ── IConversationMemory ──────────────────────────────────────────────

    public async Task StoreAsync(string userId, MemoryEntry entry, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(entry);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO ConversationMemories
                (Id, UserId, Type, Content, EntityKey, CreatedAt, ExpiresAt, Relevance)
            VALUES
                (@Id, @UserId, @Type, @Content, @EntityKey, @CreatedAt, @ExpiresAt, @Relevance)
            """;

        cmd.Parameters.AddWithValue("@Id", entry.Id);
        cmd.Parameters.AddWithValue("@UserId", userId);
        cmd.Parameters.AddWithValue("@Type", (int)entry.Type);
        cmd.Parameters.AddWithValue("@Content", entry.Content);
        cmd.Parameters.AddWithValue("@EntityKey", (object?)entry.EntityKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAt", entry.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@ExpiresAt", entry.ExpiresAt.ToString("o"));
        cmd.Parameters.AddWithValue("@Relevance", entry.Relevance);

        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogDebug("Stored memory {MemoryId} ({Type}) for user {UserId}",
            entry.Id, entry.Type, userId);
    }

    public async Task<IReadOnlyList<MemoryEntry>> RecallAsync(
        string userId,
        string? query = null,
        int maxResults = 5,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Prune expired entries lazily
        await PruneExpiredAsync(conn, ct);

        var now = DateTimeOffset.UtcNow.ToString("o");

        // Build query — optionally rank by keyword/entity overlap
        var keywords = ParseKeywords(query);
        string sql;

        if (keywords.Count > 0)
        {
            // Score = sum of keyword hits in Content + EntityKey, weighted by Relevance
            var caseClauses = string.Join(" + ",
                keywords.Select((_, i) => $"(CASE WHEN Content LIKE @kw{i} THEN 1 ELSE 0 END + CASE WHEN EntityKey LIKE @kw{i} THEN 2 ELSE 0 END)"));

            sql = $"""
                SELECT Id, UserId, Type, Content, EntityKey, CreatedAt, ExpiresAt, Relevance
                FROM ConversationMemories
                WHERE UserId = @UserId AND ExpiresAt > @Now
                ORDER BY ({caseClauses}) * Relevance DESC, CreatedAt DESC
                LIMIT @Limit
                """;
        }
        else
        {
            sql = """
                SELECT Id, UserId, Type, Content, EntityKey, CreatedAt, ExpiresAt, Relevance
                FROM ConversationMemories
                WHERE UserId = @UserId AND ExpiresAt > @Now
                ORDER BY Relevance DESC, CreatedAt DESC
                LIMIT @Limit
                """;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@UserId", userId);
        cmd.Parameters.AddWithValue("@Now", now);
        cmd.Parameters.AddWithValue("@Limit", maxResults);

        for (int i = 0; i < keywords.Count; i++)
            cmd.Parameters.AddWithValue($"@kw{i}", $"%{keywords[i]}%");

        var results = new List<MemoryEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new MemoryEntry(
                Id: reader.GetString(0),
                UserId: reader.GetString(1),
                Type: (MemoryType)reader.GetInt32(2),
                Content: reader.GetString(3),
                EntityKey: reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedAt: DateTimeOffset.Parse(reader.GetString(5)),
                ExpiresAt: DateTimeOffset.Parse(reader.GetString(6)),
                Relevance: reader.GetFloat(7)
            ));
        }

        _logger.LogDebug("Recalled {Count} memories for user {UserId} (query: {Query})",
            results.Count, userId, query ?? "(none)");

        return results;
    }

    public async Task ForgetAsync(string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ConversationMemories WHERE UserId = @UserId";
        cmd.Parameters.AddWithValue("@UserId", userId);

        var deleted = await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("Purged {Count} memory entries for user {UserId}", deleted, userId);
    }

    public async Task ForgetEntryAsync(string userId, string memoryId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(memoryId);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ConversationMemories WHERE UserId = @UserId AND Id = @Id";
        cmd.Parameters.AddWithValue("@UserId", userId);
        cmd.Parameters.AddWithValue("@Id", memoryId);

        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogDebug("Deleted memory {MemoryId} for user {UserId}", memoryId, userId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static async Task PruneExpiredAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ConversationMemories WHERE ExpiresAt <= @Now";
        cmd.Parameters.AddWithValue("@Now", DateTimeOffset.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Extract meaningful keywords from a query string for relevance scoring.
    /// Filters out common stop words and short tokens.
    /// </summary>
    internal static List<string> ParseKeywords(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "is", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did", "will", "would", "could",
            "should", "may", "might", "shall", "can", "need", "dare", "ought",
            "used", "to", "of", "in", "for", "on", "with", "at", "by", "from",
            "as", "into", "through", "during", "before", "after", "above", "below",
            "between", "out", "off", "over", "under", "again", "further", "then",
            "once", "here", "there", "when", "where", "why", "how", "all", "each",
            "every", "both", "few", "more", "most", "other", "some", "such", "no",
            "nor", "not", "only", "own", "same", "so", "than", "too", "very",
            "just", "because", "but", "and", "or", "if", "while", "about", "what",
            "which", "who", "whom", "this", "that", "these", "those", "am", "it",
            "its", "my", "me", "i", "we", "our", "you", "your", "he", "she", "they",
            "them", "his", "her", "tell", "show", "give", "get", "make"
        };

        var tokens = query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length >= 3 && !stopWords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(7) // limit SQL complexity
            .ToList();

        // Include the full trimmed query as a phrase keyword for exact-match boosting
        // Only if we already have individual tokens (avoids pure stop-word queries)
        var trimmed = query.Trim();
        if (tokens.Count > 0 && trimmed.Contains(' ') && !tokens.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            tokens.Insert(0, trimmed);

        return tokens;
    }

    public void Dispose()
    {
        // SqliteConnection pooling handles cleanup — nothing to dispose
    }
}

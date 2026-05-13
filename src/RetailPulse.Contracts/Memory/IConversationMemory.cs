namespace RetailPulse.Contracts.Memory;

/// <summary>
/// Categories of memory entries, each with a different default TTL.
/// </summary>
public enum MemoryType
{
    /// <summary>Brief summary of a conversation turn (default TTL: 30 days).</summary>
    ConversationSummary,

    /// <summary>Explicit user preference signal (default TTL: 90 days).</summary>
    UserPreference,

    /// <summary>Brand, region, channel, or time-period mention (default TTL: 30 days).</summary>
    EntityMention
}

/// <summary>
/// A single unit of conversation memory (summary, preference, or entity mention).
/// </summary>
public record MemoryEntry(
    string Id,
    string UserId,
    MemoryType Type,
    string Content,
    string? EntityKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    float Relevance = 1.0f
);

/// <summary>
/// Per-user conversation memory — stores summaries, entity mentions, and
/// preferences so agents can recall context across sessions.
/// Implementations must be thread-safe for concurrent users.
/// </summary>
public interface IConversationMemory
{
    /// <summary>Persist a single memory entry for a user.</summary>
    Task StoreAsync(string userId, MemoryEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Recall the most relevant memories for a user.
    /// When <paramref name="query"/> is provided, results are ranked by keyword
    /// and entity overlap; otherwise the most recent entries are returned.
    /// Expired entries are pruned automatically.
    /// </summary>
    Task<IReadOnlyList<MemoryEntry>> RecallAsync(
        string userId,
        string? query = null,
        int maxResults = 5,
        CancellationToken ct = default);

    /// <summary>Purge all memory entries for a user ("forget everything").</summary>
    Task ForgetAsync(string userId, CancellationToken ct = default);

    /// <summary>Remove a single memory entry for a user.</summary>
    Task ForgetEntryAsync(string userId, string memoryId, CancellationToken ct = default);
}

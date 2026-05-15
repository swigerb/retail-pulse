using RetailPulse.Contracts;
using RetailPulse.Contracts.Memory;

namespace RetailPulse.Api.Memory;

/// <summary>
/// Plugs conversation memory into the agent pipeline:
/// <list type="number">
///   <item>Before: recall relevant memories and inject into the system prompt as context.</item>
///   <item>After: extract and store summaries, entity mentions, and preferences.</item>
/// </list>
/// Memory injection is capped at ~500 tokens to avoid bloating the context window.
/// </summary>
public class ConversationMemoryMiddleware
{
    private readonly IConversationMemory _memory;
    private readonly MemoryExtractionService _extraction;
    private readonly ILogger<ConversationMemoryMiddleware> _logger;

    /// <summary>Maximum characters of memory context injected (≈500 tokens).</summary>
    private const int _maxContextChars = 2000;

    public ConversationMemoryMiddleware(
        IConversationMemory memory,
        MemoryExtractionService extraction,
        ILogger<ConversationMemoryMiddleware> logger)
    {
        _memory = memory;
        _extraction = extraction;
        _logger = logger;
    }

    /// <summary>
    /// Recall relevant memories and format as a context block for the system prompt.
    /// Returns null if no memories exist for the user.
    /// </summary>
    public async Task<string?> BuildMemoryContextAsync(
        string userId,
        string currentMessage,
        CancellationToken ct = default)
    {
        var memories = await _memory.RecallAsync(userId, currentMessage, maxResults: 5, ct);

        if (memories.Count == 0)
            return null;

        var lines = new List<string>();
        var totalChars = 0;

        foreach (var mem in memories)
        {
            var age = FormatAge(DateTimeOffset.UtcNow - mem.CreatedAt);
            var line = mem.Type switch
            {
                MemoryType.ConversationSummary => $"- You previously discussed: {mem.Content} ({age})",
                MemoryType.UserPreference => $"- User preference: {mem.Content}",
                MemoryType.EntityMention => $"- Previously mentioned: {mem.EntityKey ?? mem.Content} ({age})",
                _ => $"- {mem.Content}"
            };

            if (totalChars + line.Length > _maxContextChars)
                break;

            lines.Add(line);
            totalChars += line.Length;
        }

        if (lines.Count == 0)
            return null;

        var block = $"""

            ## User Context (from memory)
            {string.Join("\n", lines)}
            """;

        _logger.LogDebug("Injecting {Count} memory items ({Chars} chars) for user {UserId}",
            lines.Count, totalChars, userId);

        return block;
    }

    /// <summary>
    /// Extract and store memory entries from a completed exchange.
    /// Runs fire-and-forget style — failures are logged but don't block the response.
    /// </summary>
    public async Task ExtractAndStoreAsync(
        string userId,
        string userMessage,
        string assistantReply,
        CancellationToken ct = default)
    {
        try
        {
            var entries = await _extraction.ExtractAsync(userId, userMessage, assistantReply, ct);

            foreach (var entry in entries)
            {
                await _memory.StoreAsync(userId, entry, ct);
            }

            _logger.LogDebug("Stored {Count} memory entries for user {UserId}", entries.Count, userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract/store memory for user {UserId} — skipping", userId);
        }
    }

    /// <summary>
    /// Formats a timespan as a human-readable age string.
    /// </summary>
    internal static string FormatAge(TimeSpan age)
    {
        return age.TotalMinutes < 60
            ? "just now"
            : age.TotalHours < 24
            ? $"{(int)age.TotalHours}h ago"
            : age.TotalDays < 7 ? $"{(int)age.TotalDays}d ago" : $"{(int)(age.TotalDays / 7)}w ago";
    }
}

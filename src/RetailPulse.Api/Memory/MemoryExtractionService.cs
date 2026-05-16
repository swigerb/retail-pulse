using System.Text.Json;
using Microsoft.Extensions.AI;
using RetailPulse.Contracts.Memory;

namespace RetailPulse.Api.Memory;

/// <summary>
/// Uses a lightweight LLM call to extract conversation summaries, entity mentions,
/// and preference signals after each turn. Extracted entries are stored as
/// individual <see cref="MemoryEntry"/> records.
/// </summary>
public class MemoryExtractionService
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<MemoryExtractionService> _logger;

    /// <summary>Default TTL for conversation summaries and entity mentions.</summary>
    public static readonly TimeSpan ConversationTtl = TimeSpan.FromDays(30);

    /// <summary>Default TTL for user preference signals.</summary>
    public static readonly TimeSpan PreferenceTtl = TimeSpan.FromDays(90);

    private const string _extractionPrompt = """
        You are a memory extraction system. Analyze the user–assistant exchange below
        and return a JSON object with these fields:

        {
          "summary": "One-sentence summary of what was discussed.",
          "entities": ["Brand X", "Southeast", "Q4"],
          "preference": "User preference signal, or null if none detected."
        }

        Rules:
        - "summary" is ALWAYS a single sentence (max 100 words).
        - "entities" are brand names, region names, channels, time periods, or product names mentioned.
          Return an empty array if none are clearly mentioned.
        - "preference" captures signals like "I usually focus on the Southeast" or "I care about
          premium brands". Return null if no clear preference is stated.
        - Return ONLY valid JSON. No markdown fences, no explanation.

        ## Exchange
        User: {user_message}
        Assistant: {assistant_reply}
        """;

    public MemoryExtractionService(IChatClient chatClient, ILogger<MemoryExtractionService> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
    }

    /// <summary>
    /// Extracts memory entries from a single user–assistant exchange.
    /// Returns 1–N entries (summary + entity mentions + optional preference).
    /// </summary>
    public async Task<IReadOnlyList<MemoryEntry>> ExtractAsync(
        string userId,
        string userMessage,
        string assistantReply,
        CancellationToken ct = default)
    {
        var entries = new List<MemoryEntry>();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        try
        {
            string prompt = _extractionPrompt
                .Replace("{user_message}", Truncate(userMessage, 500))
                .Replace("{assistant_reply}", Truncate(assistantReply, 500));

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "You extract structured memory from conversations. Respond with JSON only."),
                new(ChatRole.User, prompt)
            };

            var options = new ChatOptions
            {
                Temperature = 0.1f,
                ResponseFormat = ChatResponseFormat.Json
            };

            ChatResponse response = await _chatClient.GetResponseAsync(messages, options, ct);
            string json = response.Text ?? "";

            ExtractionResult extraction = ParseExtraction(json, _logger);

            // 1. Conversation summary
            if (!string.IsNullOrWhiteSpace(extraction.Summary))
            {
                entries.Add(new MemoryEntry(
                    Id: Guid.NewGuid().ToString("N"),
                    UserId: userId,
                    Type: MemoryType.ConversationSummary,
                    Content: extraction.Summary,
                    EntityKey: null,
                    CreatedAt: now,
                    ExpiresAt: now.Add(ConversationTtl)
                ));
            }

            // 2. Entity mentions
            foreach (string entity in extraction.Entities)
            {
                entries.Add(new MemoryEntry(
                    Id: Guid.NewGuid().ToString("N"),
                    UserId: userId,
                    Type: MemoryType.EntityMention,
                    Content: $"Mentioned {entity}",
                    EntityKey: entity,
                    CreatedAt: now,
                    ExpiresAt: now.Add(ConversationTtl),
                    Relevance: 0.8f
                ));
            }

            // 3. User preference
            if (!string.IsNullOrWhiteSpace(extraction.Preference))
            {
                entries.Add(new MemoryEntry(
                    Id: Guid.NewGuid().ToString("N"),
                    UserId: userId,
                    Type: MemoryType.UserPreference,
                    Content: extraction.Preference,
                    EntityKey: null,
                    CreatedAt: now,
                    ExpiresAt: now.Add(PreferenceTtl),
                    Relevance: 1.2f
                ));
            }

            _logger.LogDebug(
                "Extracted {Count} memory entries for user {UserId}: summary={HasSummary}, entities={EntityCount}, preference={HasPref}",
                entries.Count, userId, !string.IsNullOrEmpty(extraction.Summary),
                extraction.Entities.Count, !string.IsNullOrEmpty(extraction.Preference));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory extraction failed for user {UserId} — skipping", userId);
        }

        return entries;
    }

    // ── Parsing ──────────────────────────────────────────────────────────

    internal static ExtractionResult ParseExtraction(string json, ILogger? logger = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string? summary = root.TryGetProperty("summary", out JsonElement sp) ? sp.GetString() : null;

            var entities = new List<string>();
            if (root.TryGetProperty("entities", out JsonElement ep) && ep.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in ep.EnumerateArray())
                {
                    string? val = item.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                        entities.Add(val);
                }
            }

            string? preference = root.TryGetProperty("preference", out JsonElement pp) && pp.ValueKind != JsonValueKind.Null
                ? pp.GetString()
                : null;

            return new ExtractionResult(summary ?? "", entities, preference);
        }
        catch (JsonException ex)
        {
            logger?.LogDebug(ex, "Failed to parse {Type}", nameof(ExtractionResult));
            return new ExtractionResult("", [], null);
        }
    }

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "…";

    internal record ExtractionResult(
        string Summary,
        IReadOnlyList<string> Entities,
        string? Preference);
}

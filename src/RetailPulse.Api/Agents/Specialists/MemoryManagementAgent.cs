using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Memory;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Agents.Specialists;

/// <summary>
/// Handles memory-management style requests.
/// Destructive requests clear user memory; misrouted "remember ..." requests are
/// treated as explicit store operations as a defense-in-depth safeguard.
/// </summary>
public class MemoryManagementAgent : ISpecialistAgent
{
    private static readonly string[] _destructiveKeywords = ["forget", "clear", "reset", "start fresh"];

    private readonly IConversationMemory _memory;
    private readonly ILogger<MemoryManagementAgent> _logger;

    public string Key { get; }
    public string DisplayName { get; }
    public string Model => "none";
    public IReadOnlyList<string> SupportedIntents { get; }
    public IReadOnlyList<string> KeywordFastPaths { get; }

    public MemoryManagementAgent(
        IConversationMemory memory,
        ILogger<MemoryManagementAgent> logger,
        AgentDefinition? definition = null)
    {
        _memory = memory;
        _logger = logger;

        // Prefer the configured AgentDefinition when supplied so the memory-management
        // taxonomy stays declared in prompts.yaml. Fall back to the previous hardcoded
        // defaults when composed without config (unit tests, ad-hoc harnesses).
        Key = string.IsNullOrWhiteSpace(definition?.Key) ? "memory-management" : definition.Key;
        DisplayName = string.IsNullOrWhiteSpace(definition?.DisplayName)
            ? "Memory Management"
            : definition.EffectiveDisplayName;
        SupportedIntents = (definition?.Intents is { Count: > 0 })
            ? [.. definition.Intents]
            : [AgentIntent.MemoryManagement];
        KeywordFastPaths = (definition?.KeywordFastPaths is { Count: > 0 })
            ? [.. definition.KeywordFastPaths]
            : _defaultMemoryKeywords;
    }

    /// <summary>Legacy hardcoded fast-paths — preserved when no config is provided.</summary>
    private static readonly string[] _defaultMemoryKeywords =
    [
        "remember that", "remember this", "forget", "clear my", "clear my history",
        "clear my data", "start fresh", "reset my context", "forget what I told you",
        "what do you know about me",
    ];

    public async Task<ChatResponse> HandleAsync(ChatRequest request, CancellationToken ct = default)
    {
        string sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
        string userId = request.User?.ObjectId ?? "anonymous";
        string message = request.Message?.Trim() ?? string.Empty;

        _logger.LogInformation("Memory management request from user {UserId}: {Message}",
            userId, request.Message);

        if (IsStoreIntent(message))
        {
            string storedContent = ExtractRememberedContent(message);
            string summary = SummarizeStoredContent(storedContent);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var entry = new MemoryEntry(
                Guid.NewGuid().ToString("N"),
                userId,
                MemoryType.UserPreference,
                storedContent,
                null,
                now,
                now.AddDays(90),
                1.2f);

            await _memory.StoreAsync(userId, entry, ct);

            return new ChatResponse(
                $"Got it — I'll remember that {summary}.",
                sessionId,
                Spans:
                [
                    new AgentSpan(
                        "Memory Management", "response",
                        "Stored explicit user memory request",
                        DurationMs: 0,
                        Timestamp: DateTimeOffset.UtcNow,
                        SessionId: sessionId)
                ]);
        }

        if (IsDestructiveIntent(message))
        {
            await _memory.ForgetAsync(userId, ct);

            return new ChatResponse(
                "Done — I've cleared all memory of our previous conversations. We're starting fresh.",
                sessionId,
                Spans:
                [
                    new AgentSpan(
                        "Memory Management", "response",
                        "Cleared all user memory",
                        DurationMs: 0,
                        Timestamp: DateTimeOffset.UtcNow,
                        SessionId: sessionId)
                ]);
        }

        return new ChatResponse(
            "I can clear memory when you ask to forget or reset it, and I can remember things when you explicitly say to remember them.",
            sessionId,
            Spans:
            [
                new AgentSpan(
                    "Memory Management", "response",
                    "No memory action taken",
                    DurationMs: 0,
                    Timestamp: DateTimeOffset.UtcNow,
                    SessionId: sessionId)
            ]);
    }

    private static bool IsStoreIntent(string message)
    {
        string lower = message.ToLowerInvariant();
        return lower.StartsWith("remember") || lower.Contains("remember that") || lower.Contains("remember this");
    }

    private static bool IsDestructiveIntent(string message)
    {
        string lower = message.ToLowerInvariant();
        return _destructiveKeywords.Any(lower.Contains);
    }

    private static string ExtractRememberedContent(string message)
    {
        string trimmed = message.Trim();
        string lower = trimmed.ToLowerInvariant();

        return lower.StartsWith("remember that ")
            ? trimmed[14..].Trim()
            : lower.StartsWith("remember this ")
            ? trimmed[14..].Trim()
            : lower.StartsWith("remember ")
            ? trimmed[9..].Trim()
            : trimmed;
    }

    private static string SummarizeStoredContent(string content)
    {
        string summary = content.Trim().TrimEnd('.', '!', '?');
        return summary.Length == 0
            ? "you asked me to keep that in mind"
            : summary.Length <= 80 ? summary : summary[..77].TrimEnd() + "...";
    }
}

using RetailPulse.Contracts;
using RetailPulse.Contracts.Memory;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Agents.Specialists;

/// <summary>
/// Handles memory management intents: "forget everything", "clear my history",
/// "start fresh", etc. Calls <see cref="IConversationMemory.ForgetAsync"/> and
/// responds with a confirmation.
/// </summary>
public class MemoryManagementAgent : ISpecialistAgent
{
    private readonly IConversationMemory _memory;
    private readonly ILogger<MemoryManagementAgent> _logger;

    public string Key => "memory-management";
    public string DisplayName => "Memory Management";
    public string Model => "none";
    public IReadOnlyList<string> SupportedIntents { get; } = [AgentIntent.MemoryManagement];

    public MemoryManagementAgent(
        IConversationMemory memory,
        ILogger<MemoryManagementAgent> logger)
    {
        _memory = memory;
        _logger = logger;
    }

    public async Task<ChatResponse> HandleAsync(ChatRequest request, CancellationToken ct = default)
    {
        string sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
        string userId = request.User?.ObjectId ?? "anonymous";

        _logger.LogInformation("Memory management request from user {UserId}: {Message}",
            userId, request.Message);

        await _memory.ForgetAsync(userId, ct);

        string reply = "Done — I've cleared all memory of our previous conversations. We're starting fresh.";

        return new ChatResponse(
            reply,
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
}

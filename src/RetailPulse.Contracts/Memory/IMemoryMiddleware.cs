namespace RetailPulse.Contracts.Memory;

/// <summary>
/// Middleware that injects conversation memories into agent prompts
/// and extracts new memories from agent responses.
/// </summary>
public interface IMemoryMiddleware
{
    /// <summary>
    /// Inject relevant memories into the system prompt before an agent call.
    /// Returns the augmented system prompt.
    /// </summary>
    Task<string> InjectMemoriesAsync(string userId, string systemPrompt, CancellationToken ct = default);

    /// <summary>
    /// Extract and store memories from an agent response.
    /// Detects conversation summaries, entity mentions, and user preferences.
    /// </summary>
    Task ExtractAndStoreAsync(string userId, string userMessage, string agentResponse, CancellationToken ct = default);

    /// <summary>
    /// Detect if the user is requesting to forget all memories.
    /// </summary>
    bool IsForgetIntent(string userMessage);

    /// <summary>
    /// Maximum token budget for injected memories (~500 tokens).
    /// </summary>
    int MaxMemoryTokens { get; }
}

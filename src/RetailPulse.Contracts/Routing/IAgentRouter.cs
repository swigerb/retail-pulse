namespace RetailPulse.Contracts.Routing;

/// <summary>
/// Routes incoming user messages to the appropriate specialist agent
/// based on intent classification. Implementations use LLM-based or
/// rule-based classification to determine intent and map it to a
/// registered specialist.
/// </summary>
public interface IAgentRouter
{
    /// <summary>
    /// Classifies the user's message and returns a routing decision
    /// indicating which specialist agent should handle it.
    /// </summary>
    /// <param name="message">The user's message text.</param>
    /// <param name="conversationHistory">Prior turns for context.</param>
    /// <param name="user">Authenticated user identity (may be null).</param>
    /// <param name="tenantId">Tenant identifier for tenant-aware routing.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A routing decision with agent key, intent, and confidence.</returns>
    Task<RoutingDecision> RouteAsync(
        string message,
        IReadOnlyList<ChatHistoryMessage>? conversationHistory,
        UserContext? user,
        string? tenantId,
        CancellationToken ct = default);
}

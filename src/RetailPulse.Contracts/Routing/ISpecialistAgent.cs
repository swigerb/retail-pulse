namespace RetailPulse.Contracts.Routing;

/// <summary>
/// Contract implemented by every specialist agent in the routing pipeline.
/// Each specialist owns a domain (e.g., demand forecasting, sentiment analysis)
/// and handles messages classified to that domain by the <see cref="IAgentRouter"/>.
/// </summary>
public interface ISpecialistAgent
{
    /// <summary>
    /// Unique key used for DI registration and routing lookup.
    /// Convention: lowercase kebab-case (e.g., "general", "demand-forecasting").
    /// </summary>
    string Key { get; }

    /// <summary>
    /// Human-readable display name shown in telemetry and UI.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// The LLM model this agent uses, sourced from <see cref="AgentDefinition.Model"/>.
    /// Used for accurate cost tracking. Returns "none" for agents that don't call an LLM.
    /// </summary>
    string Model { get; }

    /// <summary>
    /// The intent categories this specialist handles.
    /// Used by the router to validate routing decisions.
    /// </summary>
    IReadOnlyList<string> SupportedIntents { get; }

    /// <summary>
    /// Processes a chat request and returns a response.
    /// The specialist applies its own system prompt, tools, and temperature.
    /// </summary>
    Task<ChatResponse> HandleAsync(ChatRequest request, CancellationToken ct = default);
}

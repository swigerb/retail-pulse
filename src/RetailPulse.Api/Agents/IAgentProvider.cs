namespace RetailPulse.Api.Agents;

/// <summary>
/// Resolved metadata for a Foundry-hosted agent.
/// </summary>
/// <param name="Id">The runtime agent ID (e.g., asst_abc123).</param>
/// <param name="FriendlyName">Human-readable name configured at registration.</param>
/// <param name="Runtime">The agent runtime (e.g., "Azure AI Foundry").</param>
public sealed record AgentInfo(string Id, string FriendlyName, string Runtime);

/// <summary>
/// Provides typed, DI-friendly access to a Foundry-hosted agent.
/// The generic type parameter <typeparamref name="TAgent"/> is a marker interface
/// that uniquely identifies which agent this provider resolves (e.g.,
/// <see cref="IDistributionAnalysisAgent"/>).
///
/// Resolution is lazy and cached — the first call to any method triggers
/// a name-based lookup via PersistentAgentsClient, and the resolved
/// <c>asst_*</c> ID is reused for all subsequent calls.
/// </summary>
public interface IAgentProvider<TAgent> where TAgent : class
{
    /// <summary>
    /// Returns the resolved agent ID (asst_* format).
    /// </summary>
    Task<string> GetAgentIdAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the PersistentAgentsClient for thread/run operations.
    /// </summary>
    Azure.AI.Agents.Persistent.PersistentAgentsClient GetAgentsClient();

    /// <summary>
    /// Returns the resolved agent metadata.
    /// </summary>
    Task<AgentInfo> GetAgentInfoAsync(CancellationToken ct = default);
}

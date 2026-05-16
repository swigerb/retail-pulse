using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Agents;

/// <summary>
/// Marker interface for specialist agents that support predictive tool prefetching.
/// When the router identifies a prefetchable agent and entities are extracted from
/// the query, the endpoint calls <see cref="HandleWithPrefetchAsync"/> instead of
/// the standard <see cref="ISpecialistAgent.HandleAsync"/>.
/// </summary>
public interface IPrefetchableAgent : ISpecialistAgent
{
    /// <summary>
    /// Processes a chat request with pre-fetched tool data injected into the pipeline.
    /// The prefetched results are appended to the system prompt so the LLM can
    /// synthesize directly without calling those tools — saving one full roundtrip.
    /// </summary>
    Task<ChatResponse> HandleWithPrefetchAsync(
        ChatRequest request,
        IReadOnlyDictionary<string, string>? prefetchedData,
        CancellationToken ct = default);
}

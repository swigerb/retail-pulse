namespace RetailPulse.Api.Rag;

/// <summary>
/// Result of a per-agent RAG retrieval (issue #105). Carries the injectable
/// grounding block plus the metadata the endpoint uses to emit the
/// <c>rag.retrieve</c> trace span (source, chunk count, latency).
/// </summary>
/// <param name="Context">
/// Formatted grounding block ready for injection as a system message, or
/// <c>null</c> when no relevant hits were found, all hits were dropped by
/// Content Safety, or retrieval was disabled for this agent.
/// </param>
/// <param name="Enabled">
/// True when the agent's binding permits retrieval. False signals the
/// short-circuit path — the knowledge provider was never called and no
/// activity was created.
/// </param>
/// <param name="Scoped">True when the search was constrained to named sources.</param>
/// <param name="Sources">Named-source values (or empty for unscoped searches).</param>
/// <param name="AgentKey">Routing key of the agent that requested retrieval.</param>
/// <param name="ChunkCount">Number of chunks kept in <see cref="Context"/>.</param>
/// <param name="DurationMs">Elapsed retrieval time in milliseconds.</param>
/// <param name="BudgetTrimmedChunks">Chunks dropped by the ADR-006 budget.</param>
public readonly record struct RagRetrievalOutcome(
    string? Context,
    bool Enabled,
    bool Scoped,
    IReadOnlyList<string> Sources,
    string AgentKey,
    int ChunkCount,
    double DurationMs,
    int BudgetTrimmedChunks)
{
    /// <summary>Materializes the well-defined "retrieval disabled" outcome.</summary>
    public static RagRetrievalOutcome Skipped(string agentKey) => new(
        Context: null,
        Enabled: false,
        Scoped: false,
        Sources: [],
        AgentKey: agentKey,
        ChunkCount: 0,
        DurationMs: 0,
        BudgetTrimmedChunks: 0);
}

namespace RetailPulse.Api.Rag;

/// <summary>
/// How the abstraction layer should behave when the configured
/// <see cref="RetailPulse.Contracts.Rag.IKnowledgeBase"/> provider is
/// unreachable — at startup or at query time.
///
/// The two options are the only supported policies. Silently returning an
/// empty result set is explicitly NOT a supported behavior — an empty result
/// means "no matching documents in the corpus" and can never be conflated with
/// "the backend is down".
/// </summary>
public enum KnowledgeDegradationMode
{
    /// <summary>
    /// Surface the failure. Startup fails; query-time failures propagate to the
    /// caller (endpoints return 5xx). This is the safest default when a cloud
    /// provider is intentionally configured — the operator wants to know it is
    /// broken, not have the platform silently limp along.
    /// </summary>
    FailLoud = 0,

    /// <summary>
    /// Log a prominent warning and swap the active provider to the always-available
    /// in-memory BM25 knowledge base. Suitable when the operator prefers
    /// availability over cloud fidelity and understands that the corpus falls
    /// back to whatever the local seeder loaded.
    /// </summary>
    FallbackToInMemory = 1,
}

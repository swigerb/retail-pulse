namespace RetailPulse.Api.Rag;

/// <summary>
/// Selects which <see cref="RetailPulse.Contracts.Rag.IKnowledgeBase"/>
/// implementation the API uses at runtime. Bound from configuration through
/// <see cref="KnowledgeProviderOptions"/>.
///
/// Only <see cref="InMemory"/> is implemented in this repository — the cloud
/// modes are registered by their respective opt-in packages (#103, #104) and
/// fail loudly at startup when their configuration is selected but their
/// registration is not present.
/// </summary>
public enum KnowledgeProviderMode
{
    /// <summary>
    /// Volatile in-memory BM25 knowledge base. Zero cloud dependencies, works on
    /// a laptop, ships as the default.
    /// </summary>
    InMemory = 0,

    /// <summary>
    /// Azure AI Search (opt-in, requires provisioning). Implementation lives in
    /// a separate issue (#103).
    /// </summary>
    AzureAISearch = 1,

    /// <summary>
    /// Azure AI Foundry IQ knowledge (opt-in). Implementation lives in a
    /// separate issue (#104).
    /// </summary>
    FoundryIQ = 2,
}

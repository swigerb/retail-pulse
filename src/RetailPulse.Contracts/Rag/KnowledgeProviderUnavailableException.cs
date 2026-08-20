namespace RetailPulse.Contracts.Rag;

/// <summary>
/// Thrown by an <see cref="IKnowledgeBase"/> implementation to signal that its
/// backend is unreachable or unauthenticated — a transient / configuration
/// availability failure that the degradation policy layer knows how to react to.
///
/// A provider MUST use this exception rather than returning an empty result
/// when it cannot serve a request. Empty results are reserved for a genuinely
/// empty corpus. This distinction lets the degradation policy either fail
/// loudly (surface the outage) or fall back to the in-memory provider — but
/// never silently pretend the corpus is empty.
/// </summary>
public sealed class KnowledgeProviderUnavailableException : Exception
{
    /// <summary>Stable identifier of the provider that failed (e.g. "AzureAISearch").</summary>
    public string ProviderName { get; }

    public KnowledgeProviderUnavailableException(string providerName, string message)
        : base(message)
    {
        ProviderName = providerName;
    }

    public KnowledgeProviderUnavailableException(string providerName, string message, Exception innerException)
        : base(message, innerException)
    {
        ProviderName = providerName;
    }
}

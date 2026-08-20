namespace RetailPulse.Api.Rag;

/// <summary>
/// Extension seam that lets opt-in provider modules (Azure AI Search, Foundry
/// IQ) add themselves to the shared <see cref="KnowledgeProviderRegistry"/>
/// without editing <c>Program.cs</c>. The registry factory enumerates every
/// registered contribution at construction time so the order in which the
/// extension methods are called does not matter.
/// </summary>
public interface IKnowledgeProviderContribution
{
    /// <summary>Registers this contribution's provider factory on the shared registry.</summary>
    void Register(KnowledgeProviderRegistry registry);
}

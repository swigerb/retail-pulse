using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Rag;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Tests.Rag;

/// <summary>
/// Runs the shared <see cref="KnowledgeBaseConformanceTests"/> suite against
/// the in-memory BM25 provider. Future provider issues (#103 Azure AI Search,
/// #104 Foundry IQ) add their own subclass and get the same coverage floor.
/// </summary>
public sealed class InMemoryKnowledgeBaseConformanceTests : KnowledgeBaseConformanceTests
{
    protected override Task<IKnowledgeBase> CreateProviderAsync()
    {
        InMemoryKnowledgeBase kb = new(
            NullLoggerFactory.Instance.CreateLogger<InMemoryKnowledgeBase>(),
            Options.Create(new KnowledgeOptions()));
        return Task.FromResult<IKnowledgeBase>(kb);
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Rag.FoundryIQ;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Tests.Rag.FoundryIQ;

/// <summary>
/// Runs the shared <see cref="KnowledgeBaseConformanceTests"/> against the
/// Foundry IQ provider using the hand-rolled <see cref="FakeFoundryIQClient"/>.
/// Because <see cref="KnowledgeBaseCapabilities.SupportsMutation"/> is
/// <c>false</c> for Foundry IQ, the shared suite:
///   1. Skips the ingest/list/delete/scoped-search fixtures that require a
///      writable corpus (they self-guard on the capability).
///   2. Runs the read-only surface: capability shape, empty-corpus search,
///      probe.
///   3. Runs the read-only mutation-throws contract test.
/// This is the executable proof that Foundry IQ passes the shared conformance
/// suite on the parts of the contract that apply.
/// </summary>
public sealed class FoundryIQConformanceTests : KnowledgeBaseConformanceTests
{
    protected override Task<IKnowledgeBase> CreateProviderAsync()
    {
        var fake = new FakeFoundryIQClient();
        fake.Stores["vs_direct"] = new FoundryIQVectorStoreInfo("vs_direct", "retail-corpus", "Completed");
        fake.AgentsByName["retail-pulse-foundry-iq-retrieval"] =
            new FoundryIQAgentInfo("asst_retrieval", "retail-pulse-foundry-iq-retrieval");

        var options = new FoundryIQOptions
        {
            ProjectEndpoint = "https://foundry.example/api/projects/p",
            VectorStoreId = "vs_direct",
            RetrievalAgentName = "retail-pulse-foundry-iq-retrieval",
            Model = "gpt-5.4-mini",
            RequestTimeoutMs = 5_000,
            PollIntervalMs = 50,
            MaxResults = 5,
        };
        var resolver = new FoundryIQVectorStoreResolver(fake, options, NullLogger<FoundryIQVectorStoreResolver>.Instance);
        var agentProvider = new FoundryIQRetrievalAgentProvider(fake, resolver, options, NullLogger<FoundryIQRetrievalAgentProvider>.Instance);
        var kb = new FoundryIQKnowledgeBase(
            fake, resolver, agentProvider, options,
            new KnowledgeOptions(),
            new RecordingCostTracker(),
            NullLogger<FoundryIQKnowledgeBase>.Instance);
        return Task.FromResult<IKnowledgeBase>(kb);
    }
}

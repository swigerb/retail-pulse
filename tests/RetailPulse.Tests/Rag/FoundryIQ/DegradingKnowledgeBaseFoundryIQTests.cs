using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Rag;
using RetailPulse.Api.Rag.FoundryIQ;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Tests.Rag.FoundryIQ;

/// <summary>
/// Verifies that Foundry IQ's mutation-unsupported contract is honoured by
/// the shared <see cref="DegradingKnowledgeBase"/> decorator.
///
/// <see cref="NotSupportedException"/> from the primary must propagate to the
/// caller unchanged — the degradation policy is scoped to
/// <see cref="KnowledgeProviderUnavailableException"/> ONLY, so a first-class
/// capability signal is not accidentally converted into a silent fallback
/// against a different corpus.
/// </summary>
public sealed class DegradingKnowledgeBaseFoundryIQTests
{
    private static (DegradingKnowledgeBase deg, InMemoryKnowledgeBase fallback) BuildDecorator(
        KnowledgeDegradationMode mode = KnowledgeDegradationMode.FallbackToInMemory)
    {
        var fake = new FakeFoundryIQClient();
        fake.Stores["vs_direct"] = new FoundryIQVectorStoreInfo("vs_direct", "retail-corpus", "Completed");
        fake.AgentsByName["retail-pulse-foundry-iq-retrieval"] =
            new FoundryIQAgentInfo("asst_retrieval", "retail-pulse-foundry-iq-retrieval");
        var options = new FoundryIQOptions
        {
            ProjectEndpoint = "https://foundry.example/api/projects/p",
            VectorStoreId = "vs_direct",
            Model = "gpt-5.4-mini",
        };
        var resolver = new FoundryIQVectorStoreResolver(fake, options, NullLogger<FoundryIQVectorStoreResolver>.Instance);
        var agentProvider = new FoundryIQRetrievalAgentProvider(fake, resolver, options, NullLogger<FoundryIQRetrievalAgentProvider>.Instance);
        var primary = new FoundryIQKnowledgeBase(
            fake, resolver, agentProvider, options,
            new KnowledgeOptions(),
            new RecordingCostTracker(),
            NullLogger<FoundryIQKnowledgeBase>.Instance);
        var fallback = new InMemoryKnowledgeBase(
            NullLogger<InMemoryKnowledgeBase>.Instance,
            Options.Create(new KnowledgeOptions()));
        var deg = new DegradingKnowledgeBase(
            primary, fallback, mode,
            NullLogger<DegradingKnowledgeBase>.Instance);
        return (deg, fallback);
    }

    [Fact]
    public async Task Ingest_PropagatesNotSupportedException_UnderFallbackPolicy()
    {
        (DegradingKnowledgeBase deg, _) = BuildDecorator();

        Func<Task> act = () => deg.IngestDocumentAsync("t", "c", "src");

        (await act.Should().ThrowAsync<NotSupportedException>())
            .Which.Message.Should().Contain("read-only",
                "the mutation-unsupported contract MUST reach the caller — never silently rerouted to InMemory");
    }

    [Fact]
    public async Task Delete_PropagatesNotSupportedException_UnderFallbackPolicy()
    {
        (DegradingKnowledgeBase deg, _) = BuildDecorator();

        Func<Task> act = () => deg.DeleteDocumentAsync("doc-id");

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task Ingest_PropagatesNotSupportedException_UnderFailLoudPolicy()
    {
        (DegradingKnowledgeBase deg, _) = BuildDecorator(KnowledgeDegradationMode.FailLoud);

        Func<Task> act = () => deg.IngestDocumentAsync("t", "c", "src");
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public void ActiveProviderName_ReportsFoundryIQ_BeforeProbe()
    {
        (DegradingKnowledgeBase deg, _) = BuildDecorator();
        deg.ActiveProviderName.Should().Be(FoundryIQKnowledgeBase.ProviderName);
    }
}

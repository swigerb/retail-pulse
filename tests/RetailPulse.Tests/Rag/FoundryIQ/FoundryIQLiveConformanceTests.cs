using Azure.AI.Agents.Persistent;
using Azure.Identity;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Rag.FoundryIQ;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Tests.Rag.FoundryIQ;

/// <summary>
/// Live Foundry IQ integration coverage. Every test skips cleanly with an
/// explicit reason when the environment is not configured. When configured,
/// exercises the round-trip that only Foundry can serve:
/// probe, capability shape, one search, and the read-only mutation contract.
/// </summary>
public sealed class FoundryIQLiveConformanceTests
{
    [Fact]
    public void LiveTests_AssertSkipReason_WhenUnconfigured()
    {
        bool configured = FoundryIQLiveTestConfig.IsConfigured(out string? reason);
        if (configured)
        {
            reason.Should().BeNull();
            return;
        }
        reason.Should().Be(FoundryIQLiveTestConfig.SkipReason,
            "the live suite MUST record an explicit skip reason so operators distinguish outage from unconfigured");
    }

    [LiveFoundryIqFact]
    public async Task ProbeAsync_LiveEndpoint_CompletesWithoutThrowing()
    {
        FoundryIQKnowledgeBase kb = CreateLiveKb();
        Func<Task> probe = () => kb.ProbeAsync();
        await probe.Should().NotThrowAsync();
    }

    [LiveFoundryIqFact]
    public async Task SearchAsync_LiveEndpoint_ReturnsShapeConformingResults()
    {
        FoundryIQKnowledgeBase kb = CreateLiveKb();

        IReadOnlyList<SearchResult> results = await kb.SearchAsync(
            "What does the retail merchandising standard say about planogram compliance?",
            topK: 3);

        results.Should().NotBeNull();
        results.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.DocumentId));
        results.Should().OnlyContain(r => r.Score >= 0.0 && r.Score <= 1.0,
            "Foundry file_search scores land in [0..1] — anything else is a mapping bug");
        results.Select(r => r.ChunkIndex).OrderBy(i => i).Should().BeEquivalentTo(
            Enumerable.Range(0, results.Count),
            "ChunkIndex is a per-query rank ordinal — 0, 1, 2, ... in the returned order");
    }

    [LiveFoundryIqFact]
    public async Task IngestAsync_LiveEndpoint_ThrowsNotSupportedException()
    {
        FoundryIQKnowledgeBase kb = CreateLiveKb();

        Func<Task> act = () => kb.IngestDocumentAsync("t", "c", "src");
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [LiveFoundryIqFact]
    public void GetCapabilities_LiveEndpoint_ReportsReadOnlySemantics()
    {
        FoundryIQKnowledgeBase kb = CreateLiveKb();
        KnowledgeBaseCapabilities caps = kb.GetCapabilities();
        caps.ProviderName.Should().Be(FoundryIQKnowledgeBase.ProviderName);
        caps.SupportsMutation.Should().BeFalse();
        caps.ScoreSemantics.Should().ContainEquivalentOf("not comparable");
    }

    private static FoundryIQKnowledgeBase CreateLiveKb()
    {
        string endpoint = FoundryIQLiveTestConfig.ResolveEndpoint();
        var options = new FoundryIQOptions
        {
            ProjectEndpoint = endpoint,
            VectorStoreName = FoundryIQLiveTestConfig.ResolveVectorStoreName(),
            VectorStoreId = FoundryIQLiveTestConfig.ResolveVectorStoreId() ?? string.Empty,
            Model = FoundryIQLiveTestConfig.ResolveModel(),
            RetrievalAgentName = FoundryIQLiveTestConfig.ResolveRetrievalAgentName(),
            RetrievalAgentId = FoundryIQLiveTestConfig.ResolveRetrievalAgentId() ?? string.Empty,
            RequestTimeoutMs = 120_000,
        };

        var credential = new DefaultAzureCredential();
        var accessor = new FoundryClientAccessor(credential);
        PersistentAgentsClient client = accessor.GetOrCreate(endpoint);
        var iqClient = new FoundryIQClient(client, options);
        var resolver = new FoundryIQVectorStoreResolver(iqClient, options, NullLogger<FoundryIQVectorStoreResolver>.Instance);
        var agentProvider = new FoundryIQRetrievalAgentProvider(iqClient, resolver, options, NullLogger<FoundryIQRetrievalAgentProvider>.Instance);
        return new FoundryIQKnowledgeBase(
            iqClient, resolver, agentProvider, options,
            new KnowledgeOptions(),
            new RecordingCostTracker(),
            NullLogger<FoundryIQKnowledgeBase>.Instance);
    }
}

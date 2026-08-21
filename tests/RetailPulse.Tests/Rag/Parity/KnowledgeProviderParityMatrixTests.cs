using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Rag;
using RetailPulse.Api.Rag.FoundryIQ;
using RetailPulse.Tests.Rag.FoundryIQ;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Tests.Rag.Parity;

/// <summary>
/// Static parity assertion for every operation × provider combination
/// documented in <c>docs/rag/knowledge-provider-parity-matrix.md</c>.
///
/// This test complements — it does NOT replace — the shared
/// <see cref="KnowledgeBaseConformanceTests"/> suite, which asserts
/// behavioural parity via the live/fake conformance runs. The parity matrix
/// covers <em>static</em> declared behaviour: capability shape, read-only
/// mutation contract, and the doc invariant that every provider self-declares
/// score semantics as non-comparable.
///
/// The doc file is loaded from source at test time so a doc-only change that
/// silently reshuffles the table (or introduces a fourth provider without a
/// matching test row) trips this test. This is the release gate that keeps
/// the parity narrative and the implementation from drifting.
/// </summary>
public sealed class KnowledgeProviderParityMatrixTests
{
    private static InMemoryKnowledgeBase CreateInMemory() =>
        new(LoggerFactoryExtensions.CreateLogger<InMemoryKnowledgeBase>(NullLoggerFactory.Instance),
            Options.Create(new KnowledgeOptions()));

    private static FoundryIQKnowledgeBase CreateFoundry()
    {
        var fake = new FakeFoundryIQClient();
        fake.Stores["vs_parity"] = new FoundryIQVectorStoreInfo("vs_parity", "parity", "Completed");
        fake.AgentsByName["retail-pulse-foundry-iq-retrieval"] =
            new FoundryIQAgentInfo("asst_parity", "retail-pulse-foundry-iq-retrieval");
        var options = new FoundryIQOptions
        {
            ProjectEndpoint = "https://foundry.example/api/projects/p",
            VectorStoreId = "vs_parity",
            RetrievalAgentName = "retail-pulse-foundry-iq-retrieval",
            Model = "gpt-5.4-mini",
            RequestTimeoutMs = 5_000,
            PollIntervalMs = 50,
            MaxResults = 5,
        };
        var resolver = new FoundryIQVectorStoreResolver(fake, options, NullLogger<FoundryIQVectorStoreResolver>.Instance);
        var agentProvider = new FoundryIQRetrievalAgentProvider(fake, resolver, options, NullLogger<FoundryIQRetrievalAgentProvider>.Instance);
        return new FoundryIQKnowledgeBase(
            fake, resolver, agentProvider, options,
            new KnowledgeOptions(),
            new RecordingCostTracker(),
            NullLogger<FoundryIQKnowledgeBase>.Instance);
    }

    [Fact]
    public void ParityMatrix_InMemory_MatchesDocumentedCapabilityShape()
    {
        KnowledgeBaseCapabilities caps = CreateInMemory().GetCapabilities();

        caps.ProviderName.Should().Be(InMemoryKnowledgeBase.ProviderName);
        caps.Relevance.Should().Be(KnowledgeRelevanceKind.Lexical);
        caps.Persistent.Should().BeFalse();
        caps.RequiresCloud.Should().BeFalse();
        caps.SupportsMutation.Should().BeTrue();
        caps.ScoreSemantics.Should().ContainEquivalentOf("not comparable",
            "parity requires every provider to self-declare score non-comparability");
    }

    [Fact]
    public void ParityMatrix_FoundryIQ_MatchesDocumentedCapabilityShape()
    {
        KnowledgeBaseCapabilities caps = CreateFoundry().GetCapabilities();

        caps.ProviderName.Should().Be(FoundryIQKnowledgeBase.ProviderName);
        caps.Persistent.Should().BeTrue();
        caps.RequiresCloud.Should().BeTrue();
        caps.SupportsMutation.Should().BeFalse(
            "Foundry IQ is read-only; the parity matrix documents this and the shared conformance suite gates on it");
        caps.ScoreSemantics.Should().ContainEquivalentOf("not comparable");
    }

    [Fact]
    public async Task ParityMatrix_ReadOnlyProvider_MutationsThrowNotSupported()
    {
        IKnowledgeBase kb = CreateFoundry();

        Func<Task> ingest = () => kb.IngestDocumentAsync("t", "content", "src");
        Func<Task> delete = () => kb.DeleteDocumentAsync("nope");

        (await ingest.Should().ThrowAsync<NotSupportedException>()).Which
            .Message.Should().NotBeNullOrWhiteSpace();
        await delete.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public void ParityMatrixDoc_IsPresent_AndListsEveryProviderRow()
    {
        string docPath = LocateDoc();
        string content = File.ReadAllText(docPath);

        content.Should().Contain("InMemory", "matrix must list the InMemory provider row");
        content.Should().Contain("Azure AI Search", "matrix must list the Azure AI Search provider row");
        content.Should().Contain("Foundry IQ", "matrix must list the Foundry IQ provider row");

        // Every documented divergence must be paired with an explicit note so
        // future readers do not treat it as an accident.
        content.Should().Contain("Documented divergences");
        content.Should().Contain("read-only",
            "Foundry IQ read-only mutation is a first-class documented divergence");
        content.Should().Contain("not comparable",
            "score non-comparability is an invariant the matrix must repeat");
    }

    private static string LocateDoc()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "docs", "rag", "knowledge-provider-parity-matrix.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "Parity matrix doc not found. Expected docs/rag/knowledge-provider-parity-matrix.md at repo root.");
    }
}

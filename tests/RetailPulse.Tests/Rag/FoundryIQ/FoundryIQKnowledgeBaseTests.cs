using System.Reflection;
using Azure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Rag.FoundryIQ;
using RetailPulse.Contracts.Observability;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Tests.Rag.FoundryIQ;

public sealed class FoundryIQKnowledgeBaseTests
{
    private static (FoundryIQKnowledgeBase kb, FakeFoundryIQClient fake, RecordingCostTracker cost, FoundryIQOptions options) BuildKb(
        Action<FoundryIQOptions>? configureOptions = null,
        Action<FakeFoundryIQClient>? configureClient = null)
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
        configureOptions?.Invoke(options);
        configureClient?.Invoke(fake);

        var resolver = new FoundryIQVectorStoreResolver(fake, options, NullLogger<FoundryIQVectorStoreResolver>.Instance);
        var agentProvider = new FoundryIQRetrievalAgentProvider(fake, resolver, options, NullLogger<FoundryIQRetrievalAgentProvider>.Instance);
        var cost = new RecordingCostTracker();
        var kb = new FoundryIQKnowledgeBase(
            fake, resolver, agentProvider, options,
            new KnowledgeOptions(), cost,
            NullLogger<FoundryIQKnowledgeBase>.Instance);
        return (kb, fake, cost, options);
    }

    [Fact]
    public void GetCapabilities_ReportsHonestReadOnlySemantics()
    {
        (FoundryIQKnowledgeBase kb, _, _, _) = BuildKb();

        KnowledgeBaseCapabilities caps = kb.GetCapabilities();

        caps.ProviderName.Should().Be(FoundryIQKnowledgeBase.ProviderName);
        caps.Relevance.Should().Be(KnowledgeRelevanceKind.Semantic);
        caps.Persistent.Should().BeTrue();
        caps.RequiresCloud.Should().BeTrue();
        caps.SupportsMutation.Should().BeFalse(
            "Foundry IQ's corpus lives outside Retail Pulse — the capability must signal read-only honestly");
        caps.ScoreSemantics.Should().ContainEquivalentOf("not comparable",
            "score-semantics MUST include the not-comparable clause so callers never cross-rank providers");
        caps.ScoreSemantics.Should().Contain("[0..1]",
            "score-semantics MUST include the numeric range so callers can normalize/display honestly");
        caps.ScoreSemantics.Should().Contain("ChunkIndex",
            "score-semantics MUST document that ChunkIndex is a per-query rank ordinal, not a stable id");
    }

    [Fact]
    public async Task IngestDocumentAsync_ThrowsNotSupportedException()
    {
        (FoundryIQKnowledgeBase kb, _, _, _) = BuildKb();

        Func<Task> act = () => kb.IngestDocumentAsync("title", "content", "src");
        (await act.Should().ThrowAsync<NotSupportedException>())
            .Which.Message.Should().Contain("Foundry portal");
    }

    [Fact]
    public async Task DeleteDocumentAsync_ThrowsNotSupportedException()
    {
        (FoundryIQKnowledgeBase kb, _, _, _) = BuildKb();

        Func<Task> act = () => kb.DeleteDocumentAsync("doc-id");
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task SearchAsync_MapsRunHits_AndAssignsPerQueryRankAsChunkIndex()
    {
        (FoundryIQKnowledgeBase kb, FakeFoundryIQClient fake, _, _) = BuildKb(configureClient: f =>
        {
            f.NextSearchHits.Add(new FoundryIQSearchHit("file_a", "planogram.md", 0.91, "chunk one"));
            f.NextSearchHits.Add(new FoundryIQSearchHit("file_b", "supplier.md", 0.42, "chunk two"));
        });

        IReadOnlyList<SearchResult> results = await kb.SearchAsync("shelf layout", topK: 3);

        results.Should().HaveCount(2);
        results[0].DocumentId.Should().Be("file_a");
        results[0].Title.Should().Be("planogram.md");
        results[0].Source.Should().Be("planogram.md");
        results[0].ChunkIndex.Should().Be(0);
        results[1].ChunkIndex.Should().Be(1);
    }

    [Fact]
    public async Task SearchAsync_TopKClampedToMaxResults()
    {
        (FoundryIQKnowledgeBase kb, _, _, _) = BuildKb(
            configureOptions: o => o.MaxResults = 2,
            configureClient: f =>
            {
                for (int i = 0; i < 5; i++)
                {
                    f.NextSearchHits.Add(new FoundryIQSearchHit($"file_{i}", $"file_{i}.md", 0.5, $"chunk {i}"));
                }
            });

        IReadOnlyList<SearchResult> results = await kb.SearchAsync("q", topK: 20);

        results.Should().HaveCount(2, "MaxResults is the per-query ceiling regardless of caller's topK");
    }

    [Fact]
    public async Task SearchAsync_WithSources_FiltersHitsBySourceCaseInsensitive()
    {
        (FoundryIQKnowledgeBase kb, _, _, _) = BuildKb(configureClient: f =>
        {
            f.NextSearchHits.Add(new FoundryIQSearchHit("file_a", "PlAnOgRaM.md", 0.9, "a"));
            f.NextSearchHits.Add(new FoundryIQSearchHit("file_b", "supplier.md", 0.8, "b"));
            f.NextSearchHits.Add(new FoundryIQSearchHit("file_c", "planogram.md", 0.7, "c"));
        });

        IReadOnlyList<SearchResult> results = await kb.SearchAsync(
            "q", topK: 5, sources: ["planogram.md"]);

        results.Select(r => r.DocumentId).Should().BeEquivalentTo(["file_a", "file_c"]);
        results.Should().OnlyContain(r => r.Source.Equals("planogram.md", StringComparison.OrdinalIgnoreCase),
            "the sources filter must match case-insensitively so operator-supplied file names work regardless of casing");
    }

    [Fact]
    public async Task SearchAsync_EmptySources_BehavesLikeUnscoped()
    {
        (FoundryIQKnowledgeBase kb, _, _, _) = BuildKb(configureClient: f =>
        {
            f.NextSearchHits.Add(new FoundryIQSearchHit("a", "a.md", 0.5, "a"));
            f.NextSearchHits.Add(new FoundryIQSearchHit("b", "b.md", 0.4, "b"));
        });

        IReadOnlyList<SearchResult> results = await kb.SearchAsync("q", topK: 5, sources: []);
        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchAsync_RecordsCostEvent_WhenUsageReported()
    {
        (FoundryIQKnowledgeBase kb, FakeFoundryIQClient fake, RecordingCostTracker cost, FoundryIQOptions options) =
            BuildKb(configureClient: f =>
            {
                f.NextSearchHits.Add(new FoundryIQSearchHit("a", "a.md", 0.5, "a"));
                f.PromptTokens = 42;
                f.CompletionTokens = 7;
                f.UsageReported = true;
            });

        _ = await kb.SearchAsync("q", topK: 5);

        cost.Events.Should().ContainSingle();
        UsageEvent evt = cost.Events.Single();
        evt.AgentId.Should().Be(options.CostTrackingAgentId);
        evt.Model.Should().Be(options.Model);
        evt.InputTokens.Should().Be(42);
        evt.OutputTokens.Should().Be(7);
        evt.ToolName.Should().Be("file_search");
    }

    [Fact]
    public async Task SearchAsync_SkipsCostEvent_WhenUsageNotReported()
    {
        (FoundryIQKnowledgeBase kb, _, RecordingCostTracker cost, _) = BuildKb(configureClient: f =>
        {
            f.NextSearchHits.Add(new FoundryIQSearchHit("a", "a.md", 0.5, "a"));
            f.UsageReported = false;
        });

        _ = await kb.SearchAsync("q", topK: 5);

        cost.Events.Should().BeEmpty(
            "unreported usage must skip the cost event verbatim — better a debug-log gap than fabricated numbers");
    }

    [Fact]
    public async Task SearchAsync_UnauthorizedRequestFailedException_TranslatesToProviderUnavailable()
    {
        (FoundryIQKnowledgeBase kb, FakeFoundryIQClient fake, _, _) = BuildKb();
        fake.ThrowOnSearch = new RequestFailedException(403, "Forbidden");

        Func<Task> act = () => kb.SearchAsync("q", topK: 5);

        (await act.Should().ThrowAsync<KnowledgeProviderUnavailableException>())
            .Which.Message.Should().Contain("unauthorized");
    }

    [Fact]
    public async Task SearchAsync_TransportFailureAtStatus500_TranslatesToProviderUnavailable()
    {
        (FoundryIQKnowledgeBase kb, FakeFoundryIQClient fake, _, _) = BuildKb();
        fake.ThrowOnSearch = new RequestFailedException(503, "Service Unavailable");

        Func<Task> act = () => kb.SearchAsync("q", topK: 5);

        (await act.Should().ThrowAsync<KnowledgeProviderUnavailableException>())
            .Which.Message.Should().Contain("503");
    }

    [Fact]
    public async Task ProbeAsync_UnknownVectorStoreName_TranslatesToProviderUnavailable()
    {
        (FoundryIQKnowledgeBase kb, _, _, _) = BuildKb(configureOptions: o =>
        {
            o.VectorStoreId = null;
            o.VectorStoreName = "does-not-exist";
        });

        Func<Task> act = () => kb.ProbeAsync();

        (await act.Should().ThrowAsync<KnowledgeProviderUnavailableException>())
            .Which.Message.Should().Contain("does-not-exist");
    }

    [Fact]
    public async Task ProbeAsync_Reachable_Completes()
    {
        (FoundryIQKnowledgeBase kb, _, _, _) = BuildKb();

        Func<Task> act = () => kb.ProbeAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ListDocumentsAsync_ReturnsFileMetadataFromVectorStore()
    {
        (FoundryIQKnowledgeBase kb, FakeFoundryIQClient fake, _, _) = BuildKb();
        fake.FilesByStore["vs_direct"] =
        [
            new FoundryIQVectorStoreFileInfo("file_a"),
            new FoundryIQVectorStoreFileInfo("file_b"),
        ];
        fake.FileMetadata["file_a"] = new FoundryIQFileInfo("file_a", "alpha.md", DateTime.UtcNow.AddDays(-1));
        fake.FileMetadata["file_b"] = new FoundryIQFileInfo("file_b", "beta.md", DateTime.UtcNow);

        IReadOnlyList<DocumentInfo> docs = await kb.ListDocumentsAsync();

        docs.Select(d => d.Title).Should().BeEquivalentTo(["alpha.md", "beta.md"]);
        docs.Should().OnlyContain(d => d.ChunkCount == 1,
            "Foundry does not expose chunk counts per file — the provider reports 1 honestly");
    }

    [Fact]
    public void MutationUnsupportedMessage_IsPubliclyDiscoverable()
    {
        // Guards the exact string used by support diagnostics + docs.
        FieldInfo? field = typeof(FoundryIQKnowledgeBase).GetField(
            nameof(FoundryIQKnowledgeBase.MutationUnsupportedMessage),
            BindingFlags.Public | BindingFlags.Static);
        field.Should().NotBeNull("the message must be a const so tools + docs can reference it verbatim");
        FoundryIQKnowledgeBase.MutationUnsupportedMessage.Should().Contain("read-only");
        FoundryIQKnowledgeBase.MutationUnsupportedMessage.Should().Contain("Foundry-managed vector store");
    }
}

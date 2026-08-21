using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Rag.FoundryIQ;

namespace RetailPulse.Tests.Rag.FoundryIQ;

public sealed class FoundryIQRetrievalAgentProviderTests
{
    private static (FakeFoundryIQClient, FoundryIQVectorStoreResolver, FoundryIQOptions) BuildFixture(
        string? agentId = null,
        string agentName = "retail-pulse-foundry-iq-retrieval")
    {
        var fake = new FakeFoundryIQClient();
        fake.Stores["vs_direct"] = new FoundryIQVectorStoreInfo("vs_direct", "retail-corpus", "Completed");
        var options = new FoundryIQOptions
        {
            ProjectEndpoint = "https://foundry.example/api/projects/p",
            VectorStoreId = "vs_direct",
            RetrievalAgentId = agentId ?? string.Empty,
            RetrievalAgentName = agentName,
            Model = "gpt-5.4-mini",
        };
        var resolver = new FoundryIQVectorStoreResolver(fake, options, NullLogger<FoundryIQVectorStoreResolver>.Instance);
        return (fake, resolver, options);
    }

    [Fact]
    public async Task GetOrCreateAsync_ExplicitAgentId_ShortCircuits()
    {
        (FakeFoundryIQClient fake, FoundryIQVectorStoreResolver resolver, FoundryIQOptions options) =
            BuildFixture(agentId: "asst_directbind");
        var provider = new FoundryIQRetrievalAgentProvider(fake, resolver, options, NullLogger<FoundryIQRetrievalAgentProvider>.Instance);

        string id = await provider.GetOrCreateAsync(default);

        id.Should().Be("asst_directbind");
        fake.LastCreatedAgentName.Should().BeNull("explicit RetrievalAgentId must skip creation entirely");
    }

    [Fact]
    public async Task GetOrCreateAsync_MatchesExistingAgentByName()
    {
        (FakeFoundryIQClient fake, FoundryIQVectorStoreResolver resolver, FoundryIQOptions options) =
            BuildFixture(agentName: "existing-agent");
        fake.AgentsByName["existing-agent"] = new FoundryIQAgentInfo("asst_existing", "existing-agent");
        var provider = new FoundryIQRetrievalAgentProvider(fake, resolver, options, NullLogger<FoundryIQRetrievalAgentProvider>.Instance);

        string id = await provider.GetOrCreateAsync(default);

        id.Should().Be("asst_existing");
        fake.LastCreatedAgentName.Should().BeNull(
            "an existing agent with the configured name must be reused verbatim — no duplicate");
    }

    [Fact]
    public async Task GetOrCreateAsync_CreatesAgent_WhenNoneMatch()
    {
        (FakeFoundryIQClient fake, FoundryIQVectorStoreResolver resolver, FoundryIQOptions options) =
            BuildFixture(agentName: "retail-pulse-foundry-iq-retrieval");
        var provider = new FoundryIQRetrievalAgentProvider(fake, resolver, options, NullLogger<FoundryIQRetrievalAgentProvider>.Instance);

        string id = await provider.GetOrCreateAsync(default);

        id.Should().StartWith("asst_");
        fake.LastCreatedAgentName.Should().Be("retail-pulse-foundry-iq-retrieval");
        fake.LastCreatedVectorStoreId.Should().Be("vs_direct");
    }

    [Fact]
    public async Task GetOrCreateAsync_CachesResolvedId_AcrossConcurrentCalls()
    {
        (FakeFoundryIQClient fake, FoundryIQVectorStoreResolver resolver, FoundryIQOptions options) =
            BuildFixture(agentName: "retail-pulse-foundry-iq-retrieval");
        var provider = new FoundryIQRetrievalAgentProvider(fake, resolver, options, NullLogger<FoundryIQRetrievalAgentProvider>.Instance);

        Task<string>[] tasks = [.. Enumerable.Range(0, 8).Select(_ => provider.GetOrCreateAsync(default))];
        await Task.WhenAll(tasks);

        tasks.Select(t => t.Result).Distinct().Should().ContainSingle(
            "concurrent first-callers must serialise behind the semaphore — only ONE agent created");
    }
}

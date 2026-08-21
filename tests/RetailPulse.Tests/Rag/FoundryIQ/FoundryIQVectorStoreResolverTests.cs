using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Rag.FoundryIQ;

namespace RetailPulse.Tests.Rag.FoundryIQ;

public sealed class FoundryIQVectorStoreResolverTests
{
    [Fact]
    public async Task ResolveAsync_ExactId_ReturnsIdWithoutEnumerating()
    {
        var fake = new FakeFoundryIQClient();
        fake.Stores["vs_direct"] = new FoundryIQVectorStoreInfo("vs_direct", "retail-corpus", "Completed");
        var options = new FoundryIQOptions
        {
            ProjectEndpoint = "https://foundry.example/api/projects/p",
            VectorStoreId = "vs_direct",
            Model = "gpt-5.4-mini",
        };
        var resolver = new FoundryIQVectorStoreResolver(fake, options, NullLogger<FoundryIQVectorStoreResolver>.Instance);

        string id = await resolver.ResolveAsync(default);

        id.Should().Be("vs_direct");
        fake.GetVectorStoreCalls.Should().Be(1,
            "exact-id binding must resolve without enumerating the whole project");
    }

    [Fact]
    public async Task ResolveAsync_NameLookup_MatchesFirstStore()
    {
        var fake = new FakeFoundryIQClient();
        fake.Stores["vs_a"] = new FoundryIQVectorStoreInfo("vs_a", "other", "Completed");
        fake.Stores["vs_b"] = new FoundryIQVectorStoreInfo("vs_b", "retail-corpus", "Completed");
        var options = new FoundryIQOptions
        {
            ProjectEndpoint = "https://foundry.example/api/projects/p",
            VectorStoreName = "retail-corpus",
            Model = "gpt-5.4-mini",
        };
        var resolver = new FoundryIQVectorStoreResolver(fake, options, NullLogger<FoundryIQVectorStoreResolver>.Instance);

        string id = await resolver.ResolveAsync(default);
        id.Should().Be("vs_b");
    }

    [Fact]
    public async Task ResolveAsync_NameNotFound_ThrowsFoundryIQVectorStoreNotFoundException()
    {
        var fake = new FakeFoundryIQClient();
        fake.Stores["vs_a"] = new FoundryIQVectorStoreInfo("vs_a", "other", "Completed");
        var options = new FoundryIQOptions
        {
            ProjectEndpoint = "https://foundry.example/api/projects/p",
            VectorStoreName = "retail-corpus",
            Model = "gpt-5.4-mini",
        };
        var resolver = new FoundryIQVectorStoreResolver(fake, options, NullLogger<FoundryIQVectorStoreResolver>.Instance);

        Func<Task> act = () => resolver.ResolveAsync(default);
        await act.Should().ThrowAsync<FoundryIQVectorStoreNotFoundException>()
            .WithMessage("*retail-corpus*");
    }

    [Fact]
    public async Task ResolveAsync_CachesResolvedId_AcrossConcurrentCalls()
    {
        var fake = new FakeFoundryIQClient();
        fake.Stores["vs_direct"] = new FoundryIQVectorStoreInfo("vs_direct", "retail-corpus", "Completed");
        var options = new FoundryIQOptions
        {
            ProjectEndpoint = "https://foundry.example/api/projects/p",
            VectorStoreId = "vs_direct",
            Model = "gpt-5.4-mini",
        };
        var resolver = new FoundryIQVectorStoreResolver(fake, options, NullLogger<FoundryIQVectorStoreResolver>.Instance);

        Task<string>[] tasks = [.. Enumerable.Range(0, 8)
            .Select(_ => resolver.ResolveAsync(default))];
        await Task.WhenAll(tasks);

        tasks.Select(t => t.Result).Distinct().Should().ContainSingle().Which.Should().Be("vs_direct");
        fake.GetVectorStoreCalls.Should().Be(1,
            "concurrent first-callers must serialise behind the semaphore — only ONE SDK call");
    }
}

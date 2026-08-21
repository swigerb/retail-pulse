using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Packs;
using RetailPulse.Api.Rag;

namespace RetailPulse.Tests.Packs;

/// <summary>
/// Content-hash contract for the pack-aware knowledge seed (issue #108).
/// The invariant matches the MCP server's <c>SeedIfNeeded</c>: an
/// unchanged pack is a no-op; a change to a knowledge document's body
/// or a switch of the active pack forces a refresh so operators never
/// see stale grounding after a pack update.
/// </summary>
public sealed class KnowledgeBaseSeederPackTests
{
    private static InMemoryKnowledgeBase NewKnowledgeBase()
    {
        var options = Options.Create(new KnowledgeOptions
        {
            MaxDocuments = 100,
            MaxChunks = 5000,
            MaxDocumentSizeBytes = 10_485_760,
        });
        return new InMemoryKnowledgeBase(NullLogger<InMemoryKnowledgeBase>.Instance, options);
    }

    private static LoadedPack MakePack(string name, params (string Source, string Content)[] docs)
    {
        List<PackKnowledgeDocument> knowledge =
            [.. docs.Select(d => new PackKnowledgeDocument(
                Title: Path.GetFileNameWithoutExtension(d.Source),
                Source: d.Source,
                Content: d.Content,
                RelativePath: "knowledge/" + d.Source))];

        return new LoadedPack(
            name: name,
            rootPath: Path.Combine(AppContext.BaseDirectory, "pack-fixtures", name),
            metadata: new PackMetadata { Key = name, DisplayName = name },
            tenant: new RetailPulse.Contracts.TenantConfiguration { Company = "Test", Industry = "Retail" },
            agents: new RetailPulse.Api.Models.PromptConfiguration(),
            knowledgeDocuments: knowledge,
            startingTasks: []);
    }

    [Fact]
    public async Task SeedAsync_UnchangedPack_IsNoOpAfterFirstIngest()
    {
        InMemoryKnowledgeBase kb = NewKnowledgeBase();
        LoadedPack pack = MakePack("pack-a", ("intro.md", "# Intro\n\nAlpha bravo charlie."));

        await KnowledgeBaseSeeder.SeedAsync(kb, pack, NullLogger.Instance);
        int docsAfterFirst = kb.DocumentCount;
        int chunksAfterFirst = kb.ChunkCount;

        // Second invocation with the same pack: hashes match, no changes.
        await KnowledgeBaseSeeder.SeedAsync(kb, pack, NullLogger.Instance);

        kb.DocumentCount.Should().Be(docsAfterFirst, "unchanged pack must not reseed");
        kb.ChunkCount.Should().Be(chunksAfterFirst);
    }

    [Fact]
    public async Task SeedAsync_ContentChanged_RefreshesDocument()
    {
        InMemoryKnowledgeBase kb = NewKnowledgeBase();
        LoadedPack packV1 = MakePack("pack-a",
            ("intro.md", "# Intro\n\nAlpha bravo charlie."));
        LoadedPack packV2 = MakePack("pack-a",
            ("intro.md", "# Intro\n\nAlpha bravo charlie delta echo foxtrot."));

        await KnowledgeBaseSeeder.SeedAsync(kb, packV1, NullLogger.Instance);
        kb.HasDocumentWithContent("intro.md",
            PackContentFingerprint.ComputeContentHash(packV1.KnowledgeDocuments[0].Content))
            .Should().BeTrue();

        await KnowledgeBaseSeeder.SeedAsync(kb, packV2, NullLogger.Instance);

        kb.DocumentCount.Should().Be(1, "changed content must refresh, not accumulate");
        kb.HasDocumentWithContent("intro.md",
            PackContentFingerprint.ComputeContentHash(packV2.KnowledgeDocuments[0].Content))
            .Should().BeTrue("the new content hash must be present");
        kb.HasDocumentWithContent("intro.md",
            PackContentFingerprint.ComputeContentHash(packV1.KnowledgeDocuments[0].Content))
            .Should().BeFalse("the stale hash must have been purged");
    }

    [Fact]
    public void PackFingerprint_IsStableForUnchangedPack()
    {
        // Use the shipped default pack as a stable, real fixture — the
        // fingerprint contract is asserted against the file the loader
        // actually reads, not a synthetic in-memory pack.
        PackLoader loader = PackLoader.ForDirectory(PackTestPaths.PacksRoot);
        LoadedPack a = loader.Load("default");
        LoadedPack b = loader.Load("default");

        string fpA = PackContentFingerprint.ComputePackFingerprint(a);
        string fpB = PackContentFingerprint.ComputePackFingerprint(b);

        fpA.Should().Be(fpB);
        fpA.Should().StartWith(PackContentFingerprint.Version + ":");
    }

    [Fact]
    public void PackFingerprint_DiffersAcrossShippedPacks()
    {
        PackLoader loader = PackLoader.ForDirectory(PackTestPaths.PacksRoot);
        IReadOnlyList<string> packs = loader.DiscoverPacks();
        packs.Count.Should().BeGreaterThanOrEqualTo(2);

        HashSet<string> fingerprints = [.. packs.Select(p =>
            PackContentFingerprint.ComputePackFingerprint(loader.Load(p)))];

        fingerprints.Count.Should().Be(packs.Count,
            "every shipped pack must produce a distinct fingerprint");
    }
}

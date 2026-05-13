using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Rag;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Tests.Rag;

/// <summary>
/// Tests for InMemoryKnowledgeBase:
/// Ingest, search, delete, list, HasDocument, counts, duplicate handling, thread safety.
/// </summary>
public class KnowledgeBaseTests
{
    private static InMemoryKnowledgeBase CreateKb() =>
        new(NullLoggerFactory.Instance.CreateLogger<InMemoryKnowledgeBase>());

    #region Ingest -> Document Count

    [Fact]
    public async Task Ingest_SingleDocument_IncreasesDocumentCount()
    {
        var kb = CreateKb();

        var docId = await kb.IngestDocumentAsync("Holiday Planning Guide",
            "This guide covers holiday planning for retail brands including seasonal promotions, " +
            "staffing requirements, and inventory preparation for the holiday season.",
            "test-source");

        docId.Should().NotBeNullOrWhiteSpace();
        kb.DocumentCount.Should().Be(1);
    }

    [Fact]
    public async Task Ingest_MultipleDocuments_IncreasesCount()
    {
        var kb = CreateKb();

        await kb.IngestDocumentAsync("Doc 1", "Content about retail pricing strategies.", "src1");
        await kb.IngestDocumentAsync("Doc 2", "Content about supply chain management.", "src2");
        await kb.IngestDocumentAsync("Doc 3", "Content about customer engagement.", "src3");

        kb.DocumentCount.Should().Be(3);
    }

    [Fact]
    public async Task Ingest_ReturnsUniqueDocumentIds()
    {
        var kb = CreateKb();

        var id1 = await kb.IngestDocumentAsync("Doc A", "Content A", "src");
        var id2 = await kb.IngestDocumentAsync("Doc B", "Content B", "src");

        id1.Should().NotBe(id2, "each ingested document should get a unique ID");
    }

    [Fact]
    public async Task Ingest_CreatesChunks()
    {
        var kb = CreateKb();

        await kb.IngestDocumentAsync("Long Document", string.Join("\n\n",
            Enumerable.Repeat("This is a long document with many words for testing chunking behavior.", 100)),
            "test");

        kb.ChunkCount.Should().BeGreaterThan(0,
            "ingested document should produce at least one chunk");
    }

    [Fact]
    public async Task Ingest_SetsHasDocument()
    {
        var kb = CreateKb();

        await kb.IngestDocumentAsync("My Title", "Some content.", "src");

        kb.HasDocument("My Title").Should().BeTrue();
        kb.HasDocument("Nonexistent").Should().BeFalse();
    }

    #endregion

    #region Search -- Relevant Results

    [Fact]
    public async Task Search_RelevantQuery_ReturnsMatchingChunks()
    {
        var kb = CreateKb();
        await kb.IngestDocumentAsync("Holiday Planning Guide",
            "This comprehensive guide covers holiday planning for retail brands. " +
            "Holiday season preparation includes inventory forecasting, promotional calendars, " +
            "and staffing adjustments for peak holiday traffic. Holiday holiday holiday planning.",
            "wiki");

        var results = await kb.SearchAsync("holiday");

        results.Should().NotBeEmpty("search for 'holiday' should match the holiday planning document");
        results.First().Title.Should().Be("Holiday Planning Guide");
    }

    [Fact]
    public async Task Search_IrrelevantQuery_ReturnsEmpty()
    {
        var kb = CreateKb();
        await kb.IngestDocumentAsync("Holiday Planning Guide",
            "This guide covers holiday planning for retail brands including seasonal promotions.",
            "wiki");

        var results = await kb.SearchAsync("quantum computing algorithms");

        results.Should().BeEmpty("completely irrelevant query should return no results");
    }

    [Fact]
    public async Task Search_ReturnsRankedByScore()
    {
        var kb = CreateKb();
        await kb.IngestDocumentAsync("Holiday Guide",
            "Holiday planning for retail is critical. Holiday promotions drive 40% of annual revenue. " +
            "Holiday staffing holiday inventory holiday forecasting holiday preparation.",
            "wiki");
        await kb.IngestDocumentAsync("Supply Chain",
            "Supply chain optimization reduces costs. Efficient logistics improve margins. " +
            "Warehousing and distribution supply chain supply chain supply chain management.",
            "wiki");

        var results = await kb.SearchAsync("holiday promotions");

        results.Should().NotBeEmpty();
        results.First().Title.Should().Be("Holiday Guide");
    }

    [Fact]
    public async Task Search_ReturnsScores()
    {
        var kb = CreateKb();
        await kb.IngestDocumentAsync("Pricing Strategy",
            "Effective pricing strategy involves competitive pricing analysis. " +
            "Pricing elasticity and pricing optimization are key. " +
            "Pricing models pricing frameworks pricing benchmarks pricing metrics.",
            "wiki");

        var results = await kb.SearchAsync("pricing");

        results.Should().NotBeEmpty();
        results.First().Score.Should().BeGreaterThan(0,
            "search results should include a positive relevance score");
    }

    [Fact]
    public async Task Search_ResultIncludesAllFields()
    {
        var kb = CreateKb();
        await kb.IngestDocumentAsync("Test Doc",
            "Pricing strategies pricing analysis pricing optimization pricing frameworks " +
            "pricing models pricing benchmarks pricing metrics pricing tools.",
            "my-source");

        var results = await kb.SearchAsync("pricing");

        results.Should().NotBeEmpty();
        var r = results.First();
        r.DocumentId.Should().NotBeNullOrWhiteSpace();
        r.Title.Should().Be("Test Doc");
        r.Chunk.Should().NotBeNullOrWhiteSpace();
        r.Source.Should().Be("my-source");
        r.ChunkIndex.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Search_EmptyKnowledgeBase_ReturnsEmpty()
    {
        var kb = CreateKb();

        var results = await kb.SearchAsync("anything");

        results.Should().BeEmpty("searching an empty KB should return no results");
    }

    [Fact]
    public async Task Search_TopK_LimitsOutput()
    {
        var kb = CreateKb();
        for (int i = 0; i < 20; i++)
        {
            await kb.IngestDocumentAsync($"Retail Doc {i}",
                $"This document covers retail strategy topic {i}. " +
                "Retail operations retail management retail analytics retail retail retail.",
                "src");
        }

        var results = await kb.SearchAsync("retail", topK: 3);

        results.Should().HaveCountLessThanOrEqualTo(3,
            "topK should limit the number of returned results");
    }

    #endregion

    #region Delete

    [Fact]
    public async Task Delete_RemovesDocumentAndChunks()
    {
        var kb = CreateKb();
        var docId = await kb.IngestDocumentAsync("Temporary Doc", "This document will be deleted.", "src");

        await kb.DeleteDocumentAsync(docId);

        kb.DocumentCount.Should().Be(0);
        kb.ChunkCount.Should().Be(0);
    }

    [Fact]
    public async Task Delete_OnlyRemovesTargetDocument()
    {
        var kb = CreateKb();
        var id1 = await kb.IngestDocumentAsync("Doc A",
            "Pricing analysis pricing optimization pricing strategy pricing models pricing frameworks.",
            "src");
        var id2 = await kb.IngestDocumentAsync("Doc B",
            "Marketing campaigns marketing strategy marketing analytics marketing tools " +
            "marketing automation marketing insights marketing performance.",
            "src");

        await kb.DeleteDocumentAsync(id1);

        kb.DocumentCount.Should().Be(1);

        var results = await kb.SearchAsync("marketing");
        results.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Delete_NonexistentId_DoesNotThrow()
    {
        var kb = CreateKb();

        var act = async () => await kb.DeleteDocumentAsync("nonexistent-id-12345");

        await act.Should().NotThrowAsync("deleting a nonexistent ID should be a no-op");
    }

    #endregion

    #region ListDocumentsAsync

    [Fact]
    public async Task ListDocuments_ShowsAllDocuments()
    {
        var kb = CreateKb();
        await kb.IngestDocumentAsync("Doc Alpha", "Alpha content", "src");
        await kb.IngestDocumentAsync("Doc Beta", "Beta content", "src");

        var docs = await kb.ListDocumentsAsync();

        docs.Should().HaveCount(2);
        docs.Select(d => d.Title).Should().Contain("Doc Alpha").And.Contain("Doc Beta");
    }

    [Fact]
    public async Task ListDocuments_IncludesMetadata()
    {
        var kb = CreateKb();
        await kb.IngestDocumentAsync("Metadata Test", "Some content for metadata verification.", "test-src");

        var docs = await kb.ListDocumentsAsync();

        docs.Should().ContainSingle();
        var doc = docs.First();
        doc.Id.Should().NotBeNullOrWhiteSpace();
        doc.Title.Should().Be("Metadata Test");
        doc.Source.Should().Be("test-src");
        doc.IngestedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(30));
        doc.ChunkCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ListDocuments_EmptyKB_ReturnsEmpty()
    {
        var kb = CreateKb();

        var docs = await kb.ListDocumentsAsync();

        docs.Should().BeEmpty();
    }

    #endregion

    #region Duplicate Ingestion

    [Fact]
    public async Task Ingest_DuplicateTitle_HandledGracefully()
    {
        var kb = CreateKb();

        var id1 = await kb.IngestDocumentAsync("Same Title", "Content version 1", "src");
        var id2 = await kb.IngestDocumentAsync("Same Title", "Content version 2", "src");

        kb.DocumentCount.Should().BeGreaterThanOrEqualTo(1);
    }

    #endregion

    #region Thread Safety

    [Fact]
    public async Task ConcurrentIngestion_DoesNotCorruptState()
    {
        var kb = CreateKb();

        var tasks = Enumerable.Range(0, 20).Select(i =>
            kb.IngestDocumentAsync($"Concurrent Doc {i}", $"Content for concurrent test document {i}", "src"));

        var ids = await Task.WhenAll(tasks);

        ids.Should().OnlyHaveUniqueItems("each concurrent ingestion should produce a unique ID");
        kb.DocumentCount.Should().Be(20);
    }

    [Fact]
    public async Task ConcurrentSearchAndIngest_DoesNotThrow()
    {
        var kb = CreateKb();

        for (int i = 0; i < 5; i++)
            await kb.IngestDocumentAsync($"Pre-doc {i}",
                "Pre-existing content about retail retail retail retail retail operations.", "src");

        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(kb.SearchAsync("retail"));
            tasks.Add(kb.IngestDocumentAsync($"New Doc {i}", $"New content {i}", "src"));
        }

        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync("concurrent operations should not corrupt state");
    }

    #endregion
}

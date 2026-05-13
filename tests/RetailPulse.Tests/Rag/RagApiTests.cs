using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Rag;
using RetailPulse.Contracts.Rag;
using Moq;

namespace RetailPulse.Tests.Rag;

/// <summary>
/// Integration-style tests for the RAG API surface. Since endpoints in Program.cs
/// delegate directly to InMemoryKnowledgeBase and RagContextProvider, we test those
/// types directly: upload/verify, search/verify, delete/verify, stats via
/// properties/ListDocumentsAsync, and RagContextProvider integration.
/// </summary>
public class RagApiTests
{
    private static InMemoryKnowledgeBase CreateKb() =>
        new(NullLoggerFactory.Instance.CreateLogger<InMemoryKnowledgeBase>());

    #region Upload (IngestDocumentAsync) -> Verify Document Exists

    [Fact]
    public async Task Upload_ValidContent_ReturnsDocumentId()
    {
        var kb = CreateKb();

        var docId = await kb.IngestDocumentAsync("Test Document",
            "This is test content for the knowledge base.", "upload");

        docId.Should().NotBeNullOrWhiteSpace();
        kb.DocumentCount.Should().Be(1);
        kb.HasDocument("Test Document").Should().BeTrue();
    }

    [Fact]
    public async Task Upload_LargeContent_ProducesMultipleChunks()
    {
        var kb = CreateKb();
        // Use paragraph breaks so the chunker produces multiple chunks
        var largeContent = string.Join("\n\n", Enumerable.Repeat("Content paragraph about retail operations.", 200));

        var docId = await kb.IngestDocumentAsync("Large Doc", largeContent, "upload");

        docId.Should().NotBeNullOrWhiteSpace();
        kb.ChunkCount.Should().BeGreaterThan(1, "large content with paragraph breaks should produce multiple chunks");
    }

    [Fact]
    public async Task Upload_MultipleDocuments_AllTracked()
    {
        var kb = CreateKb();

        await kb.IngestDocumentAsync("Doc A", "Content about pricing.", "src-a");
        await kb.IngestDocumentAsync("Doc B", "Content about marketing.", "src-b");
        await kb.IngestDocumentAsync("Doc C", "Content about logistics.", "src-c");

        kb.DocumentCount.Should().Be(3);
        var docs = await kb.ListDocumentsAsync();
        docs.Select(d => d.Title).Should().BeEquivalentTo(new[] { "Doc A", "Doc B", "Doc C" });
    }

    #endregion

    #region Search -> Verify Results

    [Fact]
    public async Task Search_ReturnsMatchingResults()
    {
        var kb = CreateKb();
        await kb.IngestDocumentAsync("Holiday Guide",
            "Holiday promotions drive 40% of annual revenue for retail brands. " +
            "Holiday season planning is critical for holiday success. " +
            "Holiday staffing holiday inventory holiday forecasting.",
            "wiki");

        var results = await kb.SearchAsync("holiday promotions");

        results.Should().NotBeEmpty();
        results.First().Title.Should().Be("Holiday Guide");
        results.First().Score.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsEmpty()
    {
        var kb = CreateKb();
        await kb.IngestDocumentAsync("Some Doc", "Some content about retail.", "src");

        var results = await kb.SearchAsync("");

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_RespectTopK()
    {
        var kb = CreateKb();
        for (int i = 0; i < 10; i++)
            await kb.IngestDocumentAsync($"Retail Doc {i}",
                $"This covers retail strategy retail operations retail analytics retail topic {i}.", "src");

        var results = await kb.SearchAsync("retail strategy", topK: 2);

        results.Should().HaveCountLessThanOrEqualTo(2);
    }

    #endregion

    #region Delete -> Verify Removed

    [Fact]
    public async Task Delete_RemovesDocument()
    {
        var kb = CreateKb();
        var docId = await kb.IngestDocumentAsync("To Delete", "Temporary content.", "src");
        kb.DocumentCount.Should().Be(1);

        await kb.DeleteDocumentAsync(docId);

        kb.DocumentCount.Should().Be(0);
        kb.ChunkCount.Should().Be(0);
    }

    [Fact]
    public async Task Delete_PreservesOtherDocuments()
    {
        var kb = CreateKb();
        await kb.IngestDocumentAsync("Keep",
            "Marketing campaigns marketing strategy marketing analytics marketing tools " +
            "marketing automation marketing insights marketing performance.",
            "src");
        var id2 = await kb.IngestDocumentAsync("Remove",
            "Pricing analysis pricing optimization pricing strategy.", "src");

        await kb.DeleteDocumentAsync(id2);

        kb.DocumentCount.Should().Be(1);
        kb.HasDocument("Keep").Should().BeTrue();
        kb.HasDocument("Remove").Should().BeFalse();
    }

    #endregion

    #region Stats (DocumentCount, ChunkCount, ListDocumentsAsync)

    [Fact]
    public async Task Stats_ReflectsIngestedDocuments()
    {
        var kb = CreateKb();
        await kb.IngestDocumentAsync("Doc 1", "Short content.", "src");
        await kb.IngestDocumentAsync("Doc 2", string.Join("\n\n",
            Enumerable.Repeat("Longer content for chunking.", 200)), "src");

        kb.DocumentCount.Should().Be(2);
        kb.ChunkCount.Should().BeGreaterThanOrEqualTo(2, "at least one chunk per document");

        var docs = await kb.ListDocumentsAsync();
        docs.Should().HaveCount(2);
        docs.Sum(d => d.ChunkCount).Should().Be(kb.ChunkCount);
    }

    [Fact]
    public void Stats_EmptyKB_ReturnsZeros()
    {
        var kb = CreateKb();

        kb.DocumentCount.Should().Be(0);
        kb.ChunkCount.Should().Be(0);
    }

    #endregion

    #region RagContextProvider Integration

    [Fact]
    public async Task RagContextProvider_WithMatchingContent_ReturnsContext()
    {
        var kb = CreateKb();
        await kb.IngestDocumentAsync("Pricing Guide",
            "Dynamic pricing increases margins by 8-12%. Competitive pricing analysis is essential. " +
            "Price elasticity determines optimal price points for retail products. " +
            "Pricing strategy pricing frameworks pricing models pricing optimization.",
            "wiki");

        var provider = new RagContextProvider(kb,
            NullLoggerFactory.Instance.CreateLogger<RagContextProvider>());

        var context = await provider.GetContextAsync("How does dynamic pricing work?");

        context.Should().NotBeNullOrWhiteSpace("matching KB content should produce context");
    }

    [Fact]
    public async Task RagContextProvider_EmptyKB_ReturnsNull()
    {
        var kb = CreateKb();
        var provider = new RagContextProvider(kb,
            NullLoggerFactory.Instance.CreateLogger<RagContextProvider>());

        var context = await provider.GetContextAsync("Any question");

        context.Should().BeNull("empty KB should produce no context");
    }

    [Fact]
    public async Task RagContextProvider_IrrelevantQuery_ReturnsNull()
    {
        var kb = CreateKb();
        await kb.IngestDocumentAsync("Retail Guide",
            "Holiday promotions and seasonal staffing for retail brands.",
            "wiki");

        var provider = new RagContextProvider(kb,
            NullLoggerFactory.Instance.CreateLogger<RagContextProvider>());

        var context = await provider.GetContextAsync("quantum computing algorithms");

        context.Should().BeNull("irrelevant query should produce no context");
    }

    #endregion
}

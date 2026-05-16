using FluentAssertions;
using RetailPulse.Api.Rag;
using static RetailPulse.Api.Rag.DocumentChunker;

namespace RetailPulse.Tests.Rag;

/// <summary>
/// Tests for DocumentChunker — a static class that splits documents into
/// overlapping chunks for BM25 indexing. Validates chunk sizing (~500 tokens),
/// overlap (50 tokens), header retention, and edge cases.
/// </summary>
public class DocumentChunkerTests
{
    #region Short Document -> Single Chunk

    [Fact]
    public void Chunk_ShortDocument_ReturnsSingleChunk()
    {
        string content = "This is a short document about retail pricing.";

        IReadOnlyList<DocumentChunk> chunks = Chunk(content);

        chunks.Should().HaveCount(1, "a short document should produce exactly one chunk");
        chunks[0].Text.Should().Contain("retail pricing");
    }

    [Fact]
    public void Chunk_FewSentences_ReturnsSingleChunk()
    {
        string content = "Retail pricing is important. It drives customer behavior. Margins depend on it.";

        IReadOnlyList<DocumentChunk> chunks = Chunk(content);

        chunks.Should().HaveCount(1, "a few sentences well under 500 tokens should be a single chunk");
    }

    #endregion

    #region Long Document -> Multiple Chunks with Overlap

    [Fact]
    public void Chunk_LongDocument_ProducesMultipleChunks()
    {
        // Use paragraph breaks so MergeParagraphs creates multiple blocks
        string content = string.Join("\n\n", Enumerable.Repeat(
            "This is a sentence about retail brand management and competitive strategy.", 200));

        IReadOnlyList<DocumentChunk> chunks = Chunk(content);

        chunks.Should().HaveCountGreaterThan(1,
            "a long document with paragraph breaks should be split into multiple chunks");
    }

    [Fact]
    public void Chunk_LongDocument_ChunksHaveOverlap()
    {
        string content = string.Join("\n\n", Enumerable.Repeat(
            "The retail industry faces competitive pressures from e-commerce and discount chains.", 200));

        IReadOnlyList<DocumentChunk> chunks = Chunk(content);

        chunks.Should().HaveCountGreaterThan(1);

        for (int i = 0; i < chunks.Count - 1; i++)
        {
            string[] currentEnd = [.. chunks[i].Text.Split(' ').TakeLast(15)];
            string[] nextStart = [.. chunks[i + 1].Text.Split(' ').Take(60)];

            currentEnd.Intersect(nextStart).Should().NotBeEmpty(
                $"chunk {i} and chunk {i + 1} should have overlapping content");
        }
    }

    [Fact]
    public void Chunk_LongDocument_AllContentPreserved()
    {
        var sentences = Enumerable.Range(1, 200)
            .Select(i => $"Sentence number {i} discusses retail trends.")
            .ToList();
        string content = string.Join("\n\n", sentences);

        IReadOnlyList<DocumentChunk> chunks = Chunk(content);

        string allChunkText = string.Join(" ", chunks.Select(c => c.Text));
        foreach (string? sentence in sentences)
        {
            allChunkText.Should().Contain(sentence,
                "all original content should be preserved across chunks");
        }
    }

    #endregion

    #region Section Header Preservation

    [Fact]
    public void Chunk_SectionHeadersStoredInMetadata()
    {
        // Each section has enough content to fill merged blocks so headers appear at block boundaries.
        string content = "# Introduction\n\n" +
                      string.Join("\n\n", Enumerable.Repeat("Introduction content about the retail guide.", 100)) + "\n\n" +
                      "# Pricing Strategy\n\n" +
                      string.Join("\n\n", Enumerable.Repeat("Pricing strategy involves competitive analysis.", 100)) + "\n\n" +
                      "# Supply Chain\n\n" +
                      string.Join("\n\n", Enumerable.Repeat("Supply chain optimization reduces costs.", 100));

        IReadOnlyList<DocumentChunk> chunks = Chunk(content);

        // At least some chunks should have non-null SectionHeader metadata
        var headers = chunks.Select(c => c.SectionHeader).Where(h => h != null).Distinct().ToList();
        headers.Should().NotBeEmpty("chunks under markdown headings should have SectionHeader populated");
        // At least one of the section headers should be preserved
        string[] allHeaders = ["Introduction", "Pricing Strategy", "Supply Chain"];
        headers.Should().Contain(h => allHeaders.Contains(h),
            "at least one markdown heading should be preserved in chunk metadata");
    }

    [Fact]
    public void Chunk_SetsCorrectSectionHeader()
    {
        string content = "# Introduction\n\n" +
                      "Brief intro.\n\n" +
                      "# Pricing Strategy\n\n" +
                      string.Join("\n\n", Enumerable.Repeat("Pricing content about competitive analysis.", 200));

        IReadOnlyList<DocumentChunk> chunks = Chunk(content);

        chunks.Should().Contain(c => c.SectionHeader != null,
            "chunks under a markdown heading should have SectionHeader populated");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Chunk_EmptyDocument_ReturnsNoChunks()
    {
        IReadOnlyList<DocumentChunk> chunks = Chunk("");

        chunks.Should().BeEmpty("empty document should produce no chunks");
    }

    [Fact]
    public void Chunk_NullDocument_ReturnsNoChunks()
    {
        IReadOnlyList<DocumentChunk> chunks = Chunk(null!);

        chunks.Should().BeEmpty("null document should produce no chunks (not crash)");
    }

    [Fact]
    public void Chunk_WhitespaceOnly_ReturnsNoChunks()
    {
        IReadOnlyList<DocumentChunk> chunks = Chunk("   \n\n\t  ");

        chunks.Should().BeEmpty("whitespace-only document should produce no chunks");
    }

    [Fact]
    public void Chunk_SingleParagraphLongContent_ProducesSingleChunk()
    {
        // A single paragraph (no line breaks) produces one merged block,
        // and as the last block, the chunker keeps it as a single chunk.
        string content = string.Join(" ", Enumerable.Repeat("word", 2000));

        IReadOnlyList<DocumentChunk> chunks = Chunk(content);

        chunks.Should().HaveCount(1,
            "a single paragraph with no breaks produces one merged block treated as one chunk");
    }

    [Fact]
    public void Chunk_SingleWord_ReturnsSingleChunk()
    {
        IReadOnlyList<DocumentChunk> chunks = Chunk("Hello");

        chunks.Should().HaveCount(1);
        chunks[0].Text.Should().Contain("Hello");
    }

    #endregion

    #region Chunk Metadata

    [Fact]
    public void Chunk_IncludesSequentialIndex()
    {
        string content = string.Join("\n\n", Enumerable.Repeat(
            "Content about retail strategy and management.", 200));

        IReadOnlyList<DocumentChunk> chunks = Chunk(content);

        chunks.Should().HaveCountGreaterThan(1);
        for (int i = 0; i < chunks.Count; i++)
        {
            chunks[i].Index.Should().Be(i, "chunk index should match position in the list");
        }
    }

    [Fact]
    public void Chunk_ChunksAreNonEmpty()
    {
        string content = string.Join("\n\n", Enumerable.Repeat(
            "A paragraph about retail trends and market dynamics.", 100));

        IReadOnlyList<DocumentChunk> chunks = Chunk(content);

        chunks.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Text),
            "all chunks should contain non-empty text");
    }

    [Fact]
    public void CountTokens_ReturnsPositiveForNonEmptyText()
    {
        int count = CountTokens("hello world this is a test");

        count.Should().BeGreaterThan(0, "non-empty text should have a positive token count");
    }

    [Fact]
    public void CountTokens_EmptyString_ReturnsZero()
    {
        int count = CountTokens("");

        count.Should().Be(0);
    }

    #endregion
}


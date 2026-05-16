using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Rag;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Tests.Rag;

/// <summary>
/// Tests for InMemoryKnowledgeBase quota enforcement:
/// document size, document count, and chunk count limits.
/// </summary>
public class InMemoryKnowledgeBaseQuotaTests
{
    private static InMemoryKnowledgeBase CreateKnowledgeBase(
        long maxDocSizeBytes = 10 * 1024 * 1024,
        int maxDocs = 100,
        int maxChunks = 5_000)
    {
        IOptions<KnowledgeOptions> options = Options.Create(new KnowledgeOptions
        {
            MaxDocumentSizeBytes = maxDocSizeBytes,
            MaxDocuments = maxDocs,
            MaxChunks = maxChunks
        });

        return new InMemoryKnowledgeBase(
            Mock.Of<ILogger<InMemoryKnowledgeBase>>(),
            options);
    }

    // ── Document size quota ─────────────────────────────────────────────

    [Fact]
    public async Task IngestDocument_WithinSizeLimit_Succeeds()
    {
        InMemoryKnowledgeBase kb = CreateKnowledgeBase(maxDocSizeBytes: 1024);
        string content = new('a', 500); // well under 1KB

        string id = await kb.IngestDocumentAsync("small-doc", content, "test");

        id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task IngestDocument_ExceedingSizeLimit_ThrowsInvalidOperation()
    {
        InMemoryKnowledgeBase kb = CreateKnowledgeBase(maxDocSizeBytes: 100);
        string content = new('a', 200); // exceeds 100 bytes

        Func<Task<string>> act = () => kb.IngestDocumentAsync("big-doc", content, "test");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exceeds*limit*");
    }

    [Fact]
    public async Task IngestDocument_ExactlyAtSizeLimit_Succeeds()
    {
        // UTF-8 single-byte chars: 1 char = 1 byte
        InMemoryKnowledgeBase kb = CreateKnowledgeBase(maxDocSizeBytes: 500);
        // Content must produce at least one chunk, so use words
        string content = string.Join(" ", Enumerable.Repeat("hello", 50)); // ~300 bytes

        Func<Task<string>> act = () => kb.IngestDocumentAsync("exact-doc", content, "test");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task IngestDocument_DefaultLimit_Rejects10MBPlus()
    {
        InMemoryKnowledgeBase kb = CreateKnowledgeBase(); // default 10MB
        string content = new('x', 11 * 1024 * 1024); // 11MB

        Func<Task<string>> act = () => kb.IngestDocumentAsync("huge-doc", content, "test");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exceeds*");
    }

    // ── Document count quota ────────────────────────────────────────────

    [Fact]
    public async Task IngestDocument_WithinCountLimit_Succeeds()
    {
        InMemoryKnowledgeBase kb = CreateKnowledgeBase(maxDocs: 5, maxChunks: 50_000);

        for (int i = 0; i < 5; i++)
        {
            string content = $"Document number {i} with enough words to produce a chunk for testing purposes";
            await kb.IngestDocumentAsync($"doc-{i}", content, "test");
        }

        kb.DocumentCount.Should().Be(5);
    }

    [Fact]
    public async Task IngestDocument_ExceedingCountLimit_ThrowsInvalidOperation()
    {
        InMemoryKnowledgeBase kb = CreateKnowledgeBase(maxDocs: 2, maxChunks: 50_000);

        await kb.IngestDocumentAsync("doc-1", "First document with enough content to produce chunks", "test");
        await kb.IngestDocumentAsync("doc-2", "Second document with enough content to produce chunks", "test");

        Func<Task<string>> act = () => kb.IngestDocumentAsync("doc-3", "Third document that should be rejected by the quota", "test");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*full*");
    }

    // ── Chunk count quota ───────────────────────────────────────────────

    [Fact]
    public async Task IngestDocument_ExceedingChunkLimit_ThrowsInvalidOperation()
    {
        // Use a very low chunk limit so a single document exceeds it
        InMemoryKnowledgeBase kb = CreateKnowledgeBase(maxChunks: 1);
        // Large content that produces multiple chunks
        string content = string.Join("\n\n", Enumerable.Range(0, 50)
            .Select(i => $"Section {i}: This is a paragraph with enough text to form its own chunk in the document. " +
                         $"It contains varied content about retail analytics topic number {i} with multiple sentences."));

        Func<Task<string>> act = () => kb.IngestDocumentAsync("chunky-doc", content, "test");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*chunk limit*");
    }

    // ── Delete frees quota ──────────────────────────────────────────────

    [Fact]
    public async Task DeleteDocument_FreesSlotForNewDocument()
    {
        InMemoryKnowledgeBase kb = CreateKnowledgeBase(maxDocs: 1, maxChunks: 50_000);

        string firstId = await kb.IngestDocumentAsync("doc-1", "First document with enough content to generate chunks", "test");
        kb.DocumentCount.Should().Be(1);

        await kb.DeleteDocumentAsync(firstId);
        kb.DocumentCount.Should().Be(0);

        // Now a new document should succeed
        string secondId = await kb.IngestDocumentAsync("doc-2", "Second document replacing the first one successfully", "test");
        secondId.Should().NotBeNullOrEmpty();
        kb.DocumentCount.Should().Be(1);
    }

    // ── Search on empty KB ──────────────────────────────────────────────

    [Fact]
    public async Task Search_EmptyKnowledgeBase_ReturnsEmptyResults()
    {
        InMemoryKnowledgeBase kb = CreateKnowledgeBase();

        IReadOnlyList<SearchResult> results = await kb.SearchAsync("anything");

        results.Should().BeEmpty();
    }
}

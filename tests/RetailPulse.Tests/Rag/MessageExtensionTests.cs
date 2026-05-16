using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Rag;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Tests.Rag;

/// <summary>
/// Tests for the message extension surface. There is no MessageExtensionHandler class;
/// the endpoint delegates to IKnowledgeBase.SearchAsync and RagContextProvider.GetContextAsync.
/// We test the search behavior the message extension relies on, and the context formatting
/// provided by RagContextProvider.
/// </summary>
public class MessageExtensionTests
{
    private static InMemoryKnowledgeBase CreateKb() =>
        new(NullLoggerFactory.Instance.CreateLogger<InMemoryKnowledgeBase>(),
            Options.Create(new KnowledgeOptions()));

    private static RagContextProvider CreateProvider(IKnowledgeBase kb) =>
        new(kb, NullLoggerFactory.Instance.CreateLogger<RagContextProvider>());

    #region Search Returns Citations (via InMemoryKnowledgeBase)

    [Fact]
    public async Task Search_WithMatchingContent_ReturnsCitationsWithTitleAndScore()
    {
        InMemoryKnowledgeBase kb = CreateKb();
        await kb.IngestDocumentAsync("Holiday Planning Guide",
            "Holiday promotions drive 40% of annual revenue for retail brands. " +
            "Holiday season preparation includes inventory forecasting and staffing. " +
            "Holiday planning holiday strategy holiday analysis holiday optimization.",
            "wiki");

        IReadOnlyList<SearchResult> results = await kb.SearchAsync("holiday promotions");

        results.Should().NotBeEmpty("search should find matching content");
        SearchResult first = results[0];
        first.Title.Should().Be("Holiday Planning Guide");
        first.Chunk.Should().NotBeNullOrWhiteSpace();
        first.Score.Should().BeGreaterThan(0);
        first.Source.Should().Be("wiki");
        first.DocumentId.Should().NotBeNullOrWhiteSpace();
        first.ChunkIndex.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Search_MultipleSources_ReturnsCitationsFromMultipleDocs()
    {
        InMemoryKnowledgeBase kb = CreateKb();
        await kb.IngestDocumentAsync("Doc A",
            "Retail promotions are key to driving foot traffic and sales during peak seasons. " +
            "Promotions promotions promotions retail retail retail strategy.",
            "source-a");
        await kb.IngestDocumentAsync("Doc B",
            "Promotional strategies include discounts, bundles, and loyalty rewards for retail. " +
            "Promotions retail promotions retail promotions retail.",
            "source-b");
        await kb.IngestDocumentAsync("Doc C",
            "Supply chain logistics require careful planning and optimization. " +
            "Logistics logistics logistics warehousing distribution.",
            "source-c");

        IReadOnlyList<SearchResult> results = await kb.SearchAsync("retail promotions", topK: 5);

        results.Should().NotBeEmpty();
        results.Select(r => r.Title).Should().Contain("Doc A");
    }

    [Fact]
    public async Task Search_ResultsRankedByScore()
    {
        InMemoryKnowledgeBase kb = CreateKb();
        await kb.IngestDocumentAsync("Holiday Guide",
            "Holiday planning holiday promotions holiday season holiday revenue holiday staffing " +
            "holiday forecasting holiday preparation holiday analysis.",
            "wiki");
        await kb.IngestDocumentAsync("General Guide",
            "General retail operations and day-to-day store management procedures. " +
            "Operations management staffing scheduling inventory warehousing.",
            "wiki");

        IReadOnlyList<SearchResult> results = await kb.SearchAsync("holiday");

        results.Should().NotBeEmpty();
        if (results.Count > 1)
        {
            results.Should().BeInDescendingOrder(r => r.Score,
                "results should be ranked by relevance score descending");
        }
    }

    #endregion

    #region Empty KB Returns No Citations

    [Fact]
    public async Task Search_EmptyKB_ReturnsNoCitations()
    {
        InMemoryKnowledgeBase kb = CreateKb();

        IReadOnlyList<SearchResult> results = await kb.SearchAsync("holiday promotions");

        results.Should().BeEmpty("empty KB should return no search results");
    }

    [Fact]
    public async Task Context_EmptyKB_ReturnsNull()
    {
        InMemoryKnowledgeBase kb = CreateKb();
        RagContextProvider provider = CreateProvider(kb);

        string? context = await provider.GetContextAsync("Tell me about holiday promotions");

        context.Should().BeNull("empty KB should produce no context");
    }

    #endregion

    #region RagContextProvider -- Context Formatting

    [Fact]
    public async Task GetContext_WithRelevantContent_ReturnsFormattedContext()
    {
        InMemoryKnowledgeBase kb = CreateKb();
        await kb.IngestDocumentAsync("Pricing Guide",
            "Dynamic pricing increases margins by 8-12%. " +
            "Price elasticity determines optimal pricing for retail products. " +
            "Competitive pricing analysis is essential for market positioning. " +
            "Pricing strategy pricing frameworks pricing models pricing optimization.",
            "internal-wiki");

        RagContextProvider provider = CreateProvider(kb);

        string? context = await provider.GetContextAsync("How does dynamic pricing affect margins?");

        context.Should().NotBeNullOrWhiteSpace("relevant content should produce context");
        context.Should().Contain("pricing", "context should contain relevant content from KB");
    }

    [Fact]
    public async Task GetContext_IrrelevantQuery_ReturnsNull()
    {
        InMemoryKnowledgeBase kb = CreateKb();
        await kb.IngestDocumentAsync("Holiday Guide",
            "Holiday promotions and seasonal retail staffing strategies.",
            "wiki");

        RagContextProvider provider = CreateProvider(kb);

        string? context = await provider.GetContextAsync("quantum computing algorithms");

        context.Should().BeNull("irrelevant query should produce no context");
    }

    [Fact]
    public async Task GetContext_MultipleDocuments_IncludesRelevantContent()
    {
        InMemoryKnowledgeBase kb = CreateKb();
        await kb.IngestDocumentAsync("Pricing Doc",
            "Pricing strategies for retail include dynamic pricing and competitive analysis. " +
            "Pricing pricing pricing retail retail retail strategy strategy.",
            "wiki");
        await kb.IngestDocumentAsync("Marketing Doc",
            "Marketing campaigns drive brand awareness and customer acquisition in retail. " +
            "Marketing marketing marketing branding branding branding.",
            "wiki");

        RagContextProvider provider = CreateProvider(kb);

        string? context = await provider.GetContextAsync("retail pricing strategies");

        context.Should().NotBeNullOrWhiteSpace();
    }

    #endregion

    #region Mock-based IKnowledgeBase Tests

    [Fact]
    public async Task RagContextProvider_UsesKnowledgeBaseSearch()
    {
        var mockKb = new Mock<IKnowledgeBase>();
        mockKb.Setup(kb => kb.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new("doc-1", "Holiday Guide", "Holiday promotions drive revenue.", 0.85, "wiki", 0)
            ]);

        var provider = new RagContextProvider(mockKb.Object,
            NullLoggerFactory.Instance.CreateLogger<RagContextProvider>());

        string? context = await provider.GetContextAsync("holiday promotions");

        context.Should().NotBeNullOrWhiteSpace();
        mockKb.Verify(kb => kb.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RagContextProvider_NoResults_ReturnsNull()
    {
        var mockKb = new Mock<IKnowledgeBase>();
        mockKb.Setup(kb => kb.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var provider = new RagContextProvider(mockKb.Object,
            NullLoggerFactory.Instance.CreateLogger<RagContextProvider>());

        string? context = await provider.GetContextAsync("anything");

        context.Should().BeNull();
    }

    #endregion
}

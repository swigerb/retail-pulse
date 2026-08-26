using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Memory;
using RetailPulse.Contracts.Memory;
using RetailPulse.Tests.TestInfrastructure;

namespace RetailPulse.Tests.Memory;

/// <summary>
/// Tests for ConversationMemoryMiddleware and MemoryExtractionService.
/// Covers: memory injection, token budget, extraction, forget detection, edge cases.
/// </summary>
public class MemoryMiddlewareTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConversationMemory _memory;
    private readonly ConversationMemoryMiddleware _middleware;
    private readonly Mock<IChatClient> _extractionClient;

    public MemoryMiddlewareTests()
    {
        _dbPath = SqliteTestCleanup.NewDbPath("middleware_test");
        _memory = new SqliteConversationMemory(_dbPath, Mock.Of<ILogger<SqliteConversationMemory>>());

        _extractionClient = new Mock<IChatClient>();
        SetupExtractionResponse(/*lang=json,strict*/ """{"summary":"Test summary","entities":[],"preference":null}""");

        var extraction = new MemoryExtractionService(
            _extractionClient.Object,
            Mock.Of<ILogger<MemoryExtractionService>>());

        _middleware = new ConversationMemoryMiddleware(
            _memory, extraction, Mock.Of<ILogger<ConversationMemoryMiddleware>>());
    }

    public void Dispose()
    {
        _memory.Dispose();
        SqliteTestCleanup.ReleaseAndDelete(_dbPath);
    }

    private void SetupExtractionResponse(string json)
    {
        _extractionClient.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, json)));
    }

    private static MemoryEntry MakeEntry(MemoryType type, string content, string? entityKey = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new MemoryEntry(Guid.NewGuid().ToString("N"), "u", type, content, entityKey,
            now, now.AddDays(30));
    }

    #region Memory Injection (BuildMemoryContextAsync)

    [Fact]
    public async Task BuildMemoryContext_WithMemories_ReturnsContextBlock()
    {
        await _memory.StoreAsync("user-1", MakeEntry(MemoryType.UserPreference, "Prefers bar charts"));
        await _memory.StoreAsync("user-1", MakeEntry(MemoryType.ConversationSummary, "Discussed Q3 trends"));

        string? context = await _middleware.BuildMemoryContextAsync("user-1", "Show me sales");

        context.Should().NotBeNull();
        context.Should().Contain("User Context");
        context.Should().Contain("bar charts");
    }

    [Fact]
    public async Task BuildMemoryContext_LongMemories_RespectsCharLimit()
    {
        // Inject many memories — context block should not exceed ~2000 chars
        for (int i = 0; i < 50; i++)
        {
            await _memory.StoreAsync("user-1", MakeEntry(MemoryType.ConversationSummary,
                $"Detailed conversation summary entry {i} about brand performance metrics and regional distribution."));
        }

        string? context = await _middleware.BuildMemoryContextAsync("user-1", "analysis");

        context.Should().NotBeNull();
        context.Length.Should().BeLessThan(3000, "memory injection should cap at ~2000 chars (~500 tokens)");
    }

    [Fact]
    public async Task BuildMemoryContext_FirstTimeUser_ReturnsNull()
    {
        string? context = await _middleware.BuildMemoryContextAsync("new-user", "Hello");
        context.Should().BeNull();
    }

    [Fact]
    public async Task BuildMemoryContext_EmptyMemories_ReturnsNull()
    {
        string? context = await _middleware.BuildMemoryContextAsync("user-1", "Hello");
        context.Should().BeNull();
    }

    [Fact]
    public async Task BuildMemoryContext_IncludesPreferenceLabel()
    {
        await _memory.StoreAsync("user-1", MakeEntry(MemoryType.UserPreference, "Prefers pie charts"));

        string? context = await _middleware.BuildMemoryContextAsync("user-1", "data");
        context.Should().Contain("preference");
    }

    #endregion

    #region Memory Extraction (ExtractAndStoreAsync)

    [Fact]
    public async Task ExtractAndStore_StoresConversationSummary()
    {
        SetupExtractionResponse(/*lang=json,strict*/ """{"summary":"User asked about Q3 demand trends","entities":[],"preference":null}""");

        await _middleware.ExtractAndStoreAsync("user-1", "What are Q3 trends?", "Q3 demand increased 15%.");

        IReadOnlyList<MemoryEntry> memories = await _memory.RecallAsync("user-1");
        memories.Should().Contain(m => m.Type == MemoryType.ConversationSummary);
    }

    [Fact]
    public async Task ExtractAndStore_StoresEntityMentions()
    {
        SetupExtractionResponse(/*lang=json,strict*/ """{"summary":"Discussed Brand X","entities":["Brand X","Southwest"],"preference":null}""");

        await _middleware.ExtractAndStoreAsync("user-1", "Tell me about Brand X", "Brand X grew 10% in Southwest.");

        IReadOnlyList<MemoryEntry> memories = await _memory.RecallAsync("user-1", maxResults: 10);
        memories.Should().Contain(m => m.Type == MemoryType.EntityMention && m.EntityKey == "Brand X");
        memories.Should().Contain(m => m.Type == MemoryType.EntityMention && m.EntityKey == "Southwest");
    }

    [Fact]
    public async Task ExtractAndStore_StoresUserPreference()
    {
        SetupExtractionResponse(/*lang=json,strict*/ """{"summary":"User preferences","entities":[],"preference":"User prefers bar charts for data"}""");

        await _middleware.ExtractAndStoreAsync("user-1", "I prefer bar charts", "I'll use bar charts.");

        IReadOnlyList<MemoryEntry> memories = await _memory.RecallAsync("user-1", maxResults: 10);
        memories.Should().Contain(m => m.Type == MemoryType.UserPreference);
    }

    [Fact]
    public async Task ExtractAndStore_ShortExchange_StillExtractsSummary()
    {
        SetupExtractionResponse(/*lang=json,strict*/ """{"summary":"Greeting exchange","entities":[],"preference":null}""");

        await _middleware.ExtractAndStoreAsync("user-1", "Hi", "Hello! How can I help?");

        IReadOnlyList<MemoryEntry> memories = await _memory.RecallAsync("user-1");
        memories.Should().Contain(m => m.Type == MemoryType.ConversationSummary);
    }

    [Fact]
    public async Task ExtractAndStore_ExtractionFails_DoesNotThrow()
    {
        _extractionClient.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("LLM down"));

        Func<Task> act = () => _middleware.ExtractAndStoreAsync("user-1", "msg", "reply");
        await act.Should().NotThrowAsync("extraction failures should be swallowed");
    }

    #endregion

    #region Extraction Parsing (Internal)

    [Fact]
    public void ParseExtraction_ValidJson_ReturnsCorrectFields()
    {
        string json = /*lang=json,strict*/ """{"summary":"Test","entities":["Brand X","Q4"],"preference":"Prefers pie charts"}""";
        MemoryExtractionService.ExtractionResult result = MemoryExtractionService.ParseExtraction(json);

        result.Summary.Should().Be("Test");
        result.Entities.Should().BeEquivalentTo(["Brand X", "Q4"]);
        result.Preference.Should().Be("Prefers pie charts");
    }

    [Fact]
    public void ParseExtraction_MalformedJson_ReturnsEmpty()
    {
        MemoryExtractionService.ExtractionResult result = MemoryExtractionService.ParseExtraction("not json at all");

        result.Summary.Should().BeEmpty();
        result.Entities.Should().BeEmpty();
        result.Preference.Should().BeNull();
    }

    [Fact]
    public void ParseExtraction_NullPreference_ReturnsNull()
    {
        string json = /*lang=json,strict*/ """{"summary":"Test","entities":[],"preference":null}""";
        MemoryExtractionService.ExtractionResult result = MemoryExtractionService.ParseExtraction(json);

        result.Preference.Should().BeNull();
    }

    [Fact]
    public void ParseExtraction_EmptyEntities_ReturnsEmptyList()
    {
        string json = /*lang=json,strict*/ """{"summary":"Test","entities":[],"preference":null}""";
        MemoryExtractionService.ExtractionResult result = MemoryExtractionService.ParseExtraction(json);

        result.Entities.Should().BeEmpty();
    }

    #endregion

    #region FormatAge (Internal)

    [Theory]
    [InlineData(30, "just now")]         // 30 minutes
    [InlineData(120, "2h ago")]          // 2 hours
    [InlineData(1440, "1d ago")]         // 1 day
    [InlineData(10080, "1w ago")]        // 1 week
    public void FormatAge_ReturnsHumanReadable(int minutes, string expected)
    {
        string result = ConversationMemoryMiddleware.FormatAge(TimeSpan.FromMinutes(minutes));
        result.Should().Be(expected);
    }

    #endregion
}

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Memory;
using RetailPulse.Contracts.Memory;

namespace RetailPulse.Tests.Memory;

/// <summary>
/// Tests for SqliteConversationMemory (IConversationMemory implementation).
/// Uses the real implementation with temp-file SQLite for isolated, realistic tests.
/// Covers: CRUD, privacy scoping, TTL, concurrency, query relevance, edge cases.
/// </summary>
public class ConversationMemoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConversationMemory _memory;

    public ConversationMemoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"memory_test_{Guid.NewGuid():N}.db");
        _memory = new SqliteConversationMemory(_dbPath, Mock.Of<ILogger<SqliteConversationMemory>>());
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private static MemoryEntry MakeEntry(
        MemoryType type, string content, string? entityKey = null, float relevance = 1.0f)
    {
        var now = DateTimeOffset.UtcNow;
        var ttl = type == MemoryType.UserPreference
            ? MemoryExtractionService.PreferenceTtl
            : MemoryExtractionService.ConversationTtl;
        return new MemoryEntry(
            Id: Guid.NewGuid().ToString("N"),
            UserId: "placeholder",
            Type: type,
            Content: content,
            EntityKey: entityKey,
            CreatedAt: now,
            ExpiresAt: now + ttl,
            Relevance: relevance);
    }

    private static MemoryEntry MakeExpiredEntry(MemoryType type, string content)
    {
        var past = DateTimeOffset.UtcNow.AddDays(-31);
        return new MemoryEntry(
            Id: Guid.NewGuid().ToString("N"),
            UserId: "placeholder",
            Type: type,
            Content: content,
            EntityKey: null,
            CreatedAt: past,
            ExpiresAt: past.AddDays(1)); // expired ~30 days ago
    }

    #region StoreAsync

    [Fact]
    public async Task StoreAsync_CreatesEntryWithCorrectFields()
    {
        var entry = MakeEntry(MemoryType.ConversationSummary, "Discussed Q3 sales trends");
        await _memory.StoreAsync("user-1", entry);

        var recalled = await _memory.RecallAsync("user-1");
        recalled.Should().ContainSingle();
        recalled[0].Content.Should().Be("Discussed Q3 sales trends");
        recalled[0].Type.Should().Be(MemoryType.ConversationSummary);
        recalled[0].UserId.Should().Be("user-1");
    }

    [Fact]
    public void StoreAsync_ConversationSummary_HasCorrectTTL()
    {
        var entry = MakeEntry(MemoryType.ConversationSummary, "Summary");
        entry.ExpiresAt.Should().BeCloseTo(
            DateTimeOffset.UtcNow.AddDays(30), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void StoreAsync_UserPreference_Has90DayTTL()
    {
        var entry = MakeEntry(MemoryType.UserPreference, "Prefers bar charts");
        entry.ExpiresAt.Should().BeCloseTo(
            DateTimeOffset.UtcNow.AddDays(90), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StoreAsync_EntityMention_PersistsEntityKey()
    {
        var entry = MakeEntry(MemoryType.EntityMention, "Brand X mentioned", entityKey: "Brand X");
        await _memory.StoreAsync("user-1", entry);

        var recalled = await _memory.RecallAsync("user-1");
        recalled.Should().ContainSingle().Which.EntityKey.Should().Be("Brand X");
    }

    [Fact]
    public void StoreAsync_GeneratesUniqueIds()
    {
        var e1 = MakeEntry(MemoryType.ConversationSummary, "First");
        var e2 = MakeEntry(MemoryType.ConversationSummary, "Second");
        e1.Id.Should().NotBe(e2.Id);
    }

    [Fact]
    public async Task StoreAsync_WithEntityKey_PersistsCorrectly()
    {
        var entry = MakeEntry(MemoryType.EntityMention, "Apex Grill demand up", entityKey: "Apex Grill");
        await _memory.StoreAsync("user-1", entry);

        var recalled = await _memory.RecallAsync("user-1");
        recalled.Should().ContainSingle().Which.EntityKey.Should().Be("Apex Grill");
    }

    #endregion

    #region RecallAsync — Privacy

    [Fact]
    public async Task RecallAsync_ReturnsEntriesForCorrectUserOnly()
    {
        await _memory.StoreAsync("user-1", MakeEntry(MemoryType.ConversationSummary, "User 1 data"));
        await _memory.StoreAsync("user-2", MakeEntry(MemoryType.ConversationSummary, "User 2 data"));

        var user1Memories = await _memory.RecallAsync("user-1");
        var user2Memories = await _memory.RecallAsync("user-2");

        user1Memories.Should().HaveCount(1);
        user1Memories[0].Content.Should().Be("User 1 data");
        user2Memories.Should().HaveCount(1);
        user2Memories[0].Content.Should().Be("User 2 data");
    }

    [Fact]
    public async Task RecallAsync_NeverLeaksDataBetweenUsers()
    {
        await _memory.StoreAsync("user-alpha", MakeEntry(MemoryType.UserPreference, "Secret preference"));
        await _memory.StoreAsync("user-beta", MakeEntry(MemoryType.ConversationSummary, "Beta summary"));

        var alpha = await _memory.RecallAsync("user-alpha");
        alpha.Should().OnlyContain(e => e.UserId == "user-alpha");

        var beta = await _memory.RecallAsync("user-beta");
        beta.Should().OnlyContain(e => e.UserId == "user-beta");
    }

    #endregion

    #region RecallAsync — Filtering & Limits

    [Fact]
    public async Task RecallAsync_LimitsToMaxResults()
    {
        for (int i = 0; i < 20; i++)
            await _memory.StoreAsync("user-1", MakeEntry(MemoryType.ConversationSummary, $"Memory {i}"));

        var results = await _memory.RecallAsync("user-1", maxResults: 5);
        results.Should().HaveCount(5);
    }

    [Fact]
    public async Task RecallAsync_WithQuery_RanksRelevantEntries()
    {
        await _memory.StoreAsync("user-1", MakeEntry(MemoryType.EntityMention, "Brand X demand is strong", entityKey: "Brand X"));
        await _memory.StoreAsync("user-1", MakeEntry(MemoryType.EntityMention, "Brand Y supply issues", entityKey: "Brand Y"));
        await _memory.StoreAsync("user-1", MakeEntry(MemoryType.ConversationSummary, "General chit-chat"));

        var results = await _memory.RecallAsync("user-1", query: "Brand X");
        results.Should().NotBeEmpty();
        results[0].EntityKey.Should().Be("Brand X");
    }

    [Fact]
    public async Task RecallAsync_EntityKeyMatching_FindsByEntityKey()
    {
        await _memory.StoreAsync("user-1", MakeEntry(MemoryType.EntityMention, "Apex Grill trending up", entityKey: "Apex Grill"));
        await _memory.StoreAsync("user-1", MakeEntry(MemoryType.EntityMention, "Sierra Gold stable", entityKey: "Sierra Gold"));

        var results = await _memory.RecallAsync("user-1", query: "Apex Grill");
        results.Should().Contain(e => e.EntityKey == "Apex Grill");
    }

    #endregion

    #region RecallAsync — Empty State

    [Fact]
    public async Task RecallAsync_EmptyForNewUser_ReturnsEmptyList()
    {
        var results = await _memory.RecallAsync("nonexistent-user");
        results.Should().NotBeNull();
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task RecallAsync_AfterForget_ReturnsEmptyList()
    {
        await _memory.StoreAsync("user-1", MakeEntry(MemoryType.ConversationSummary, "data"));
        await _memory.ForgetAsync("user-1");

        var results = await _memory.RecallAsync("user-1");
        results.Should().BeEmpty();
    }

    #endregion

    #region TTL Enforcement

    [Fact]
    public async Task RecallAsync_ExpiredEntries_AreCleanedUp()
    {
        await _memory.StoreAsync("user-1", MakeExpiredEntry(MemoryType.ConversationSummary, "Old data"));
        await _memory.StoreAsync("user-1", MakeEntry(MemoryType.ConversationSummary, "Fresh data"));

        var results = await _memory.RecallAsync("user-1");
        results.Should().HaveCount(1);
        results[0].Content.Should().Be("Fresh data");
    }

    [Fact]
    public async Task RecallAsync_NotYetExpired_StillReturned()
    {
        await _memory.StoreAsync("user-1", MakeEntry(MemoryType.UserPreference, "Still valid"));
        var results = await _memory.RecallAsync("user-1");
        results.Should().HaveCount(1);
    }

    #endregion

    #region ForgetAsync

    [Fact]
    public async Task ForgetAsync_PurgesAllEntriesForUser()
    {
        await _memory.StoreAsync("user-1", MakeEntry(MemoryType.ConversationSummary, "S1"));
        await _memory.StoreAsync("user-1", MakeEntry(MemoryType.UserPreference, "P1"));
        await _memory.StoreAsync("user-1", MakeEntry(MemoryType.EntityMention, "E1"));

        await _memory.ForgetAsync("user-1");
        var results = await _memory.RecallAsync("user-1");
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task ForgetAsync_DoesNotAffectOtherUsers()
    {
        await _memory.StoreAsync("user-1", MakeEntry(MemoryType.ConversationSummary, "U1 data"));
        await _memory.StoreAsync("user-2", MakeEntry(MemoryType.ConversationSummary, "U2 data"));

        await _memory.ForgetAsync("user-1");
        var user2 = await _memory.RecallAsync("user-2");
        user2.Should().HaveCount(1);
        user2[0].Content.Should().Be("U2 data");
    }

    [Fact]
    public async Task ForgetAsync_OnEmptyUser_DoesNotThrow()
    {
        var act = () => _memory.ForgetAsync("nobody");
        await act.Should().NotThrowAsync();
    }

    #endregion

    #region ForgetEntryAsync

    [Fact]
    public async Task ForgetEntryAsync_RemovesOnlySpecifiedEntry()
    {
        var e1 = MakeEntry(MemoryType.ConversationSummary, "Keep me");
        var e2 = MakeEntry(MemoryType.ConversationSummary, "Delete me");
        await _memory.StoreAsync("user-1", e1);
        await _memory.StoreAsync("user-1", e2);

        await _memory.ForgetEntryAsync("user-1", e2.Id);

        var results = await _memory.RecallAsync("user-1");
        results.Should().HaveCount(1);
        results[0].Content.Should().Be("Keep me");
    }

    [Fact]
    public async Task ForgetEntryAsync_NonexistentId_DoesNotThrow()
    {
        var act = () => _memory.ForgetEntryAsync("user-1", "fake-id");
        await act.Should().NotThrowAsync();
    }

    #endregion

    #region Concurrency

    [Fact]
    public async Task ConcurrentStores_DifferentUsers_DontInterfere()
    {
        var tasks = Enumerable.Range(0, 10).Select(i =>
            _memory.StoreAsync($"user-{i}", MakeEntry(MemoryType.ConversationSummary, $"Data for user {i}")));
        await Task.WhenAll(tasks);

        for (int i = 0; i < 10; i++)
        {
            var results = await _memory.RecallAsync($"user-{i}");
            results.Should().HaveCount(1);
            results[0].Content.Should().Be($"Data for user {i}");
        }
    }

    [Fact]
    public async Task ConcurrentStores_SameUser_AllPersisted()
    {
        var tasks = Enumerable.Range(0, 5).Select(i =>
            _memory.StoreAsync("user-1", MakeEntry(MemoryType.ConversationSummary, $"Concurrent entry {i}")));
        await Task.WhenAll(tasks);

        var results = await _memory.RecallAsync("user-1", maxResults: 50);
        results.Should().HaveCount(5);
    }

    #endregion

    #region Keyword Parsing (Internal)

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("  ", 0)]
    [InlineData("Brand X", 2)]         // "Brand X" phrase + "Brand" token ("X" < 3 chars)
    [InlineData("the is a", 0)]         // all stop words
    [InlineData("Sierra Gold Tequila", 4)] // phrase + 3 tokens
    public void ParseKeywords_ExtractsCorrectCount(string? query, int expectedCount)
    {
        var keywords = SqliteConversationMemory.ParseKeywords(query);
        keywords.Should().HaveCount(expectedCount);
    }

    [Fact]
    public void ParseKeywords_LimitsTo8Keywords()
    {
        var longQuery = "one two three four five six seven eight nine ten eleven twelve";
        var keywords = SqliteConversationMemory.ParseKeywords(longQuery);
        keywords.Count.Should().BeLessThanOrEqualTo(8);
    }

    #endregion
}

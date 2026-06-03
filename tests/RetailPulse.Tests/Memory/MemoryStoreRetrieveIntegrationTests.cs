using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Memory;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Memory;

namespace RetailPulse.Tests.Memory;

public class MemoryStoreRetrieveIntegrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConversationMemory _memory;
    private readonly MemoryManagementAgent _agent;

    public MemoryStoreRetrieveIntegrationTests()
    {
        _dbPath = Path.Combine(AppContext.BaseDirectory, $"memory-store-retrieve-{Guid.NewGuid():N}.db");
        _memory = new SqliteConversationMemory(_dbPath, NullLogger<SqliteConversationMemory>.Instance);
        _agent = new MemoryManagementAgent(_memory, NullLogger<MemoryManagementAgent>.Instance);
    }

    [Fact]
    public async Task StoreAsync_PersistsEntry_RecallAsync_RetrievesIt()
    {
        const string userId = "user-store-retrieve";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MemoryEntry entry = CreateEntry(
            userId,
            MemoryType.EntityMention,
            "ClearDesk is trending positive",
            entityKey: "ClearDesk",
            createdAt: now,
            expiresAt: now.AddDays(30),
            relevance: 1.1f);

        await _memory.StoreAsync(userId, entry);

        IReadOnlyList<MemoryEntry> recalled = await _memory.RecallAsync(userId, query: "ClearDesk", maxResults: 10);

        recalled.Should().ContainSingle();
        recalled[0].Id.Should().Be(entry.Id);
        recalled[0].UserId.Should().Be(userId);
        recalled[0].Type.Should().Be(MemoryType.EntityMention);
        recalled[0].Content.Should().Be("ClearDesk is trending positive");
        recalled[0].EntityKey.Should().Be("ClearDesk");
    }

    [Fact]
    public async Task MemoryManagementAgent_StoreIntent_PersistsToMemory()
    {
        const string userId = "user-agent-store";

        ChatResponse response = await _agent.HandleAsync(CreateRequest(
            "Remember that ClearDesk is trending positive",
            userId,
            "session-agent-store"));

        IReadOnlyList<MemoryEntry> recalled = await _memory.RecallAsync(userId, query: "ClearDesk", maxResults: 10);

        response.Reply.Should().ContainEquivalentOf("remember");
        recalled.Should().ContainSingle();
        recalled[0].UserId.Should().Be(userId);
        recalled[0].Type.Should().Be(MemoryType.UserPreference);
        recalled[0].Content.Should().Be("ClearDesk is trending positive");
    }

    [Fact]
    public async Task MemoryManagementAgent_ForgetIntent_ClearsMemory()
    {
        const string userId = "user-agent-forget";

        await _agent.HandleAsync(CreateRequest(
            "Remember that ClearDesk is trending positive",
            userId,
            "session-agent-forget-store"));

        IReadOnlyList<MemoryEntry> beforeForget = await _memory.RecallAsync(userId, maxResults: 10);
        beforeForget.Should().ContainSingle();

        ChatResponse response = await _agent.HandleAsync(CreateRequest(
            "Forget everything",
            userId,
            "session-agent-forget-clear"));

        IReadOnlyList<MemoryEntry> afterForget = await _memory.RecallAsync(userId, maxResults: 10);

        response.Reply.Should().ContainEquivalentOf("cleared");
        afterForget.Should().BeEmpty();
    }

    [Fact]
    public async Task StoreAndRetrieve_UserId_Isolation()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MemoryEntry userOneEntry = CreateEntry(
            "user-one",
            MemoryType.ConversationSummary,
            "User one remembers Apex Grill",
            createdAt: now,
            expiresAt: now.AddDays(30));
        MemoryEntry userTwoEntry = CreateEntry(
            "user-two",
            MemoryType.UserPreference,
            "User two prefers Sierra Gold",
            createdAt: now.AddMinutes(1),
            expiresAt: now.AddDays(90));

        await _memory.StoreAsync("user-one", userOneEntry);
        await _memory.StoreAsync("user-two", userTwoEntry);

        IReadOnlyList<MemoryEntry> userOneResults = await _memory.RecallAsync("user-one", maxResults: 10);
        IReadOnlyList<MemoryEntry> userTwoResults = await _memory.RecallAsync("user-two", maxResults: 10);

        userOneResults.Should().ContainSingle();
        userOneResults[0].UserId.Should().Be("user-one");
        userOneResults[0].Content.Should().Be("User one remembers Apex Grill");

        userTwoResults.Should().ContainSingle();
        userTwoResults[0].UserId.Should().Be("user-two");
        userTwoResults[0].Content.Should().Be("User two prefers Sierra Gold");
    }

    [Fact]
    public async Task RecallAsync_FiltersExpiredEntries()
    {
        const string userId = "user-expired";
        DateTimeOffset createdAt = DateTimeOffset.UtcNow.AddDays(-2);
        MemoryEntry expiredEntry = CreateEntry(
            userId,
            MemoryType.ConversationSummary,
            "This memory is expired",
            createdAt: createdAt,
            expiresAt: createdAt.AddDays(1));

        await _memory.StoreAsync(userId, expiredEntry);

        IReadOnlyList<MemoryEntry> recalled = await _memory.RecallAsync(userId, maxResults: 10);

        recalled.Should().BeEmpty();
    }

    public void Dispose()
    {
        _memory.Dispose();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
    }

    private static MemoryEntry CreateEntry(
        string userId,
        MemoryType type,
        string content,
        string? entityKey = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? expiresAt = null,
        float relevance = 1.0f)
    {
        DateTimeOffset created = createdAt ?? DateTimeOffset.UtcNow;
        return new MemoryEntry(
            Id: Guid.NewGuid().ToString("N"),
            UserId: userId,
            Type: type,
            Content: content,
            EntityKey: entityKey,
            CreatedAt: created,
            ExpiresAt: expiresAt ?? created.AddDays(30),
            Relevance: relevance);
    }

    private static ChatRequest CreateRequest(string message, string userId, string sessionId)
        => new(
            Message: message,
            SessionId: sessionId,
            User: new UserContext(userId, "Test User", "test@example.com"));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup for SQLite sidecar files.
        }
    }
}

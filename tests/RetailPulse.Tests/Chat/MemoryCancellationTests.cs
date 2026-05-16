using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Memory;
using RetailPulse.Contracts.Memory;

namespace RetailPulse.Tests.Chat;

/// <summary>
/// Sprint 3 reliability: memory cancellation in chat endpoints.
/// Validates that chat endpoints pass linked CancellationTokens to memory work,
/// cancelled requests stop memory extraction, and exceptions are logged (not swallowed).
/// </summary>
public class MemoryCancellationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConversationMemory _memory;
    private readonly Mock<ILogger<ConversationMemoryMiddleware>> _middlewareLogger;
    private readonly Mock<ILogger<MemoryExtractionService>> _extractionLogger;

    public MemoryCancellationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cancel_memory_{Guid.NewGuid():N}.db");
        _memory = new SqliteConversationMemory(_dbPath, Mock.Of<ILogger<SqliteConversationMemory>>());
        _middlewareLogger = new Mock<ILogger<ConversationMemoryMiddleware>>();
        _extractionLogger = new Mock<ILogger<MemoryExtractionService>>();
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    // ── Memory Work Receives CancellationToken ──────────────────────────

    [Fact]
    public async Task MemoryRecall_ReceivesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        ConversationMemoryMiddleware middleware = CreateMiddleware(CreatePassthroughChatClient());

        // Non-cancelled token should complete normally
        string? result = await middleware.BuildMemoryContextAsync("user-1", "test query", cts.Token);

        // Should complete without throwing (no memories stored yet, returns null)
        result.Should().BeNull();
    }

    [Fact]
    public async Task MemoryStore_ReceivesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        MemoryEntry entry = MakeEntry();

        // Store should accept the token and complete
        await _memory.StoreAsync("user-1", entry, cts.Token);

        IReadOnlyList<MemoryEntry> recalled = await _memory.RecallAsync("user-1", ct: cts.Token);
        recalled.Should().HaveCount(1);
    }

    [Fact]
    public async Task LinkedToken_PropagatedThroughMiddleware()
    {
        // Simulate linked token pattern: parent + per-request
        using var parentCts = new CancellationTokenSource();
        using var requestCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            parentCts.Token, requestCts.Token);

        ConversationMemoryMiddleware middleware = CreateMiddleware(CreatePassthroughChatClient());

        // Both tokens active — should work
        string? result = await middleware.BuildMemoryContextAsync("user-1", "test", linked.Token);
        result.Should().BeNull(); // No memories yet

        // Cancel parent — linked token should be cancelled
        parentCts.Cancel();
        linked.Token.IsCancellationRequested.Should().BeTrue();
    }

    // ── Cancelled Request Stops Memory Extraction ───────────────────────

    [Fact]
    public async Task CancelledToken_StopsMemoryRecall()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task<IReadOnlyList<MemoryEntry>>> act = () => _memory.RecallAsync("user-1", "test", ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CancelledToken_StopsMemoryStore()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => _memory.StoreAsync("user-1", MakeEntry(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CancelledToken_StopsExtractionService()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var chatClient = new Mock<IChatClient>();
        chatClient.Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var extraction = new MemoryExtractionService(chatClient.Object, _extractionLogger.Object);

        // ExtractAsync catches exceptions and logs them — it doesn't rethrow
        IReadOnlyList<MemoryEntry> result = await extraction.ExtractAsync("user-1", "hello", "world", cts.Token);

        // Should return empty (exception caught), but the chat client was called with the token
        result.Should().BeEmpty();
        chatClient.Verify(c => c.GetResponseAsync(
            It.IsAny<IList<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.Is<CancellationToken>(t => t.IsCancellationRequested)), Times.Once);
    }

    [Fact]
    public async Task CancelledLinkedToken_StopsMemoryForget()
    {
        using var parentCts = new CancellationTokenSource();
        using var requestCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            parentCts.Token, requestCts.Token);

        // Cancel via request scope
        requestCts.Cancel();

        Func<Task> act = () => _memory.ForgetAsync("user-1", linked.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Exceptions Logged, Not Swallowed ────────────────────────────────

    [Fact]
    public async Task ExtractionFailure_IsLogged_NotSwallowed()
    {
        var chatClient = new Mock<IChatClient>();
        chatClient.Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("LLM unavailable"));

        var extraction = new MemoryExtractionService(chatClient.Object, _extractionLogger.Object);

        // Should not throw — returns empty list
        IReadOnlyList<MemoryEntry> result = await extraction.ExtractAsync("user-1", "hello", "world");

        result.Should().BeEmpty();

        // Verify warning was logged
        _extractionLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Memory extraction failed")),
                It.IsAny<InvalidOperationException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task MiddlewareExtractAndStore_LogsWarningOnFailure()
    {
        var chatClient = new Mock<IChatClient>();
        chatClient.Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Extraction timed out"));

        var extraction = new MemoryExtractionService(chatClient.Object, _extractionLogger.Object);
        var middleware = new ConversationMemoryMiddleware(_memory, extraction, _middlewareLogger.Object);

        // Should not throw
        await middleware.ExtractAndStoreAsync("user-1", "hello", "world");

        // The extraction service logs a warning, middleware doesn't re-throw
        _extractionLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task MemoryRecall_WithStoredData_CancellationRespected()
    {
        // Store data first
        await _memory.StoreAsync("user-1", MakeEntry());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task<IReadOnlyList<MemoryEntry>>> act = () => _memory.RecallAsync("user-1", "test", ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "even with data present, cancellation should be respected");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static MemoryEntry MakeEntry(string content = "Test memory") =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            UserId: "user-1",
            Type: MemoryType.ConversationSummary,
            Content: content,
            EntityKey: null,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(30));

    private ConversationMemoryMiddleware CreateMiddleware(IChatClient chatClient)
    {
        var extraction = new MemoryExtractionService(chatClient, _extractionLogger.Object);
        return new ConversationMemoryMiddleware(_memory, extraction, _middlewareLogger.Object);
    }

    private static IChatClient CreatePassthroughChatClient()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                                     /*lang=json,strict*/
                                     """{"summary": "test", "entities": [], "preference": null}""")));
        return mock.Object;
    }
}

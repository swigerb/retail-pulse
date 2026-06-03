using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Memory;

namespace RetailPulse.Tests.Memory;

public class MemoryManagementAgentTests
{
    [Fact]
    public async Task HandleAsync_RememberThat_CallsStoreAsync_NotForgetAsync()
    {
        Mock<IConversationMemory> memory = CreateMemoryMock();
        MemoryEntry? storedEntry = null;
        memory.Setup(m => m.StoreAsync("user-123", It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()))
            .Callback<string, MemoryEntry, CancellationToken>((_, entry, _) => storedEntry = entry)
            .Returns(Task.CompletedTask);

        MemoryManagementAgent agent = CreateAgent(memory);

        ChatResponse response = await agent.HandleAsync(CreateRequest("Remember that ClearDesk is trending modestly positive in the Northeast"));

        memory.Verify(m => m.StoreAsync("user-123", It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()), Times.Once);
        memory.Verify(m => m.ForgetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        storedEntry.Should().NotBeNull();
        storedEntry.Content.Should().Contain("ClearDesk").And.Contain("Northeast");
        response.Reply.Should().NotContainEquivalentOf("cleared");
    }

    [Fact]
    public async Task HandleAsync_RememberThis_CallsStoreAsync_NotForgetAsync()
    {
        Mock<IConversationMemory> memory = CreateMemoryMock();
        memory.Setup(m => m.StoreAsync("user-123", It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        MemoryManagementAgent agent = CreateAgent(memory);

        await agent.HandleAsync(CreateRequest("Remember this: margins improved 5% YoY"));

        memory.Verify(m => m.StoreAsync("user-123", It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()), Times.Once);
        memory.Verify(m => m.ForgetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("Forget everything")]
    [InlineData("Clear my history")]
    [InlineData("Start fresh")]
    public async Task HandleAsync_ClearPhrases_CallForgetAsync(string message)
    {
        Mock<IConversationMemory> memory = CreateMemoryMock();
        memory.Setup(m => m.ForgetAsync("user-123", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        MemoryManagementAgent agent = CreateAgent(memory);

        ChatResponse response = await agent.HandleAsync(CreateRequest(message));

        memory.Verify(m => m.ForgetAsync("user-123", It.IsAny<CancellationToken>()), Times.Once);
        memory.Verify(m => m.StoreAsync(It.IsAny<string>(), It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()), Times.Never);
        response.Reply.Should().ContainEquivalentOf("cleared");
    }

    [Fact]
    public async Task HandleAsync_StoreIntent_ReturnsRememberConfirmation()
    {
        Mock<IConversationMemory> memory = CreateMemoryMock();
        memory.Setup(m => m.StoreAsync("user-123", It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        MemoryManagementAgent agent = CreateAgent(memory);

        ChatResponse response = await agent.HandleAsync(CreateRequest("Remember this: margins improved 5% YoY"));

        response.Reply.Should().MatchRegex("(?i)(remember|got it)");
        response.Reply.Should().NotContainEquivalentOf("cleared");
    }

    [Fact]
    public async Task HandleAsync_StoreIntent_UsesSupportedMemoryType()
    {
        Mock<IConversationMemory> memory = CreateMemoryMock();
        MemoryEntry? storedEntry = null;
        memory.Setup(m => m.StoreAsync("user-123", It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()))
            .Callback<string, MemoryEntry, CancellationToken>((_, entry, _) => storedEntry = entry)
            .Returns(Task.CompletedTask);

        MemoryManagementAgent agent = CreateAgent(memory);

        await agent.HandleAsync(CreateRequest("Remember this: margins improved 5% YoY"));

        storedEntry.Should().NotBeNull();
        storedEntry.Type.Should().Be(MemoryType.UserPreference);
        storedEntry.Relevance.Should().Be(1.2f);
    }

    [Fact]
    public async Task HandleAsync_StoreIntent_UsesNinetyDayTtl()
    {
        Mock<IConversationMemory> memory = CreateMemoryMock();
        MemoryEntry? storedEntry = null;
        memory.Setup(m => m.StoreAsync("user-123", It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()))
            .Callback<string, MemoryEntry, CancellationToken>((_, entry, _) => storedEntry = entry)
            .Returns(Task.CompletedTask);

        MemoryManagementAgent agent = CreateAgent(memory);

        await agent.HandleAsync(CreateRequest("Remember this: margins improved 5% YoY"));

        storedEntry.Should().NotBeNull();
        storedEntry.ExpiresAt.Should().BeCloseTo(storedEntry.CreatedAt.AddDays(90), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task HandleAsync_StoreIntent_PersistsUserFactInContent()
    {
        Mock<IConversationMemory> memory = CreateMemoryMock();
        MemoryEntry? storedEntry = null;
        memory.Setup(m => m.StoreAsync("user-123", It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()))
            .Callback<string, MemoryEntry, CancellationToken>((_, entry, _) => storedEntry = entry)
            .Returns(Task.CompletedTask);

        MemoryManagementAgent agent = CreateAgent(memory);

        await agent.HandleAsync(CreateRequest("Remember that ClearDesk is trending modestly positive in the Northeast"));

        storedEntry.Should().NotBeNull();
        storedEntry.Content.Should().NotBeNullOrWhiteSpace();
        storedEntry.Content.Should().Contain("ClearDesk").And.Contain("trending");
    }

    private static MemoryManagementAgent CreateAgent(Mock<IConversationMemory> memory)
        => new(memory.Object, NullLogger<MemoryManagementAgent>.Instance);

    private static ChatRequest CreateRequest(string message)
        => new(
            message,
            SessionId: "session-123",
            User: new UserContext("user-123", "Test User", "test@example.com"));

    private static Mock<IConversationMemory> CreateMemoryMock()
        => new(MockBehavior.Loose);
}

using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Api.Memory;
using RetailPulse.Contracts.Memory;

namespace RetailPulse.Tests.Memory;

public class MemoryPreferenceExtractionTests
{
    [Fact]
    public async Task ExtractAsync_SpiritsAndTequilaExchange_ExtractsSummaryEntitiesAndPreference()
    {
        var chatClient = new Mock<IChatClient>();
        chatClient.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(
                new ChatMessage(ChatRole.Assistant,
#pragma warning disable JSON002 // Probable JSON string detected — intentional test fixture
                    """{"summary":"User expressed focus on Spirits category and premium tequila positioning","entities":["Spirits","premium tequila"],"preference":"Focused on Spirits category, especially premium tequila positioning"}"""
#pragma warning restore JSON002
                    )));

        var extraction = new MemoryExtractionService(chatClient.Object, NullLogger<MemoryExtractionService>.Instance);

        IReadOnlyList<MemoryEntry> entries = await extraction.ExtractAsync(
            "user-123",
            "I'm focused on the Spirits category, especially premium tequila positioning",
            "Got it — I'll keep the analysis centered on Spirits and premium tequila positioning.");

        entries.Should().ContainSingle(entry =>
            entry.Type == MemoryType.ConversationSummary &&
            entry.Content == "User expressed focus on Spirits category and premium tequila positioning");

        entries.Should().ContainSingle(entry =>
            entry.Type == MemoryType.EntityMention &&
            entry.EntityKey == "Spirits" &&
            entry.Content == "Mentioned Spirits");

        entries.Should().ContainSingle(entry =>
            entry.Type == MemoryType.EntityMention &&
            entry.EntityKey == "premium tequila" &&
            entry.Content == "Mentioned premium tequila");

        MemoryEntry preference = entries.Should().ContainSingle(entry =>
            entry.Type == MemoryType.UserPreference &&
            entry.Content == "Focused on Spirits category, especially premium tequila positioning").Subject;

        preference.ExpiresAt.Should().BeCloseTo(
            preference.CreatedAt.Add(MemoryExtractionService.PreferenceTtl),
            TimeSpan.FromSeconds(5));
    }
}

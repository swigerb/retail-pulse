using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Cards;
using RetailPulse.Api.Hubs;
using RetailPulse.Contracts.Cards;

namespace RetailPulse.Tests.Cards;

/// <summary>
/// Tests for comment behavior on collaborative Adaptive Cards.
/// Covers: add comment, multiple comments ordering, comment on archived card.
/// </summary>
public class CardCommentTests
{
    private readonly InMemoryAdaptiveCardState _state;

    public CardCommentTests()
    {
        _state = new InMemoryAdaptiveCardState(CreateMockHub(), Mock.Of<ILogger<InMemoryAdaptiveCardState>>());
    }

    [Fact]
    public async Task Comment_AddsCommentWithTimestamp()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("Comment Card"));
        CardAction action = MakeCommentAction("user-1", "Great analysis!");

        AdaptiveCard updated = await _state.ActionAsync(card.Id, action);

        updated.Comments.Should().HaveCount(1);
        updated.Comments[0].Text.Should().Be("Great analysis!");
        updated.Comments[0].UserId.Should().Be("user-1");
        updated.Comments[0].Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Comment_MultipleComments_PreservesOrder()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("Multi Comment"));
        await _state.ActionAsync(card.Id, MakeCommentAction("user-1", "First"));
        await _state.ActionAsync(card.Id, MakeCommentAction("user-2", "Second"));
        AdaptiveCard updated = await _state.ActionAsync(card.Id, MakeCommentAction("user-3", "Third"));

        updated.Comments.Should().HaveCount(3);
        updated.Comments[0].Text.Should().Be("First");
        updated.Comments[1].Text.Should().Be("Second");
        updated.Comments[2].Text.Should().Be("Third");
    }

    [Fact]
    public async Task Comment_OnArchivedCard_ThrowsInvalidOperation()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("Archived"));
        await _state.ArchiveAsync(card.Id);

        CardAction action = MakeCommentAction("user-1", "Late comment");
        Func<Task<AdaptiveCard>> act = () => _state.ActionAsync(card.Id, action);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Comment_PreservesUserName()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("UserName Comment"));
        var action = new CardAction("user-1", "Bob Jones", CardActionType.Comment, new() { ["text"] = "Hello" });
        AdaptiveCard updated = await _state.ActionAsync(card.Id, action);

        updated.Comments[0].UserName.Should().Be("Bob Jones");
    }

    [Fact]
    public async Task Comment_EmptyText_DoesNotAddComment()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("Empty Comment"));
        CardAction action = MakeCommentAction("user-1", "");
        AdaptiveCard updated = await _state.ActionAsync(card.Id, action);

        updated.Comments.Should().BeEmpty();
    }

    [Fact]
    public async Task Comment_WhitespaceOnly_DoesNotAddComment()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("Whitespace Comment"));
        CardAction action = MakeCommentAction("user-1", "   ");
        AdaptiveCard updated = await _state.ActionAsync(card.Id, action);

        updated.Comments.Should().BeEmpty();
    }

    [Fact]
    public async Task Comment_DoesNotAffectLifecycle()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("Lifecycle Comment"));
        card.Lifecycle.Should().Be(CardLifecycle.Active);

        AdaptiveCard updated = await _state.ActionAsync(card.Id, MakeCommentAction("user-1", "Comment"));

        updated.Lifecycle.Should().Be(CardLifecycle.Active);
    }

    [Fact]
    public async Task Comment_SameUserMultipleTimes_AllPreserved()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("Repeat Commenter"));
        await _state.ActionAsync(card.Id, MakeCommentAction("user-1", "First thought"));
        AdaptiveCard updated = await _state.ActionAsync(card.Id, MakeCommentAction("user-1", "Second thought"));

        updated.Comments.Should().HaveCount(2);
    }

    #region Helpers

    private static CreateCardRequest MakeRequest(string title)
        => new(title, CardType.Dashboard, "test-user", []);

    private static CardAction MakeCommentAction(string userId, string text)
        => new(userId, $"User {userId}", CardActionType.Comment, new() { ["text"] = text });

    private static IHubContext<TelemetryHub> CreateMockHub()
    {
        var mockClients = new Mock<IHubClients>();
        var mockProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.All).Returns(mockProxy.Object);
        var mockHub = new Mock<IHubContext<TelemetryHub>>();
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
        return mockHub.Object;
    }

    #endregion
}

using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Cards;
using RetailPulse.Api.Hubs;
using RetailPulse.Contracts.Cards;

namespace RetailPulse.Tests.Cards;

/// <summary>
/// Tests for voting behavior on collaborative Adaptive Cards.
/// Covers: vote adds, idempotency, lifecycle transitions, split vote escalation, tally accuracy.
/// </summary>
public class CardVotingTests
{
    private readonly InMemoryAdaptiveCardState _state;

    public CardVotingTests()
    {
        _state = new InMemoryAdaptiveCardState(CreateMockHub(), Mock.Of<ILogger<InMemoryAdaptiveCardState>>());
    }

    [Fact]
    public async Task Vote_AddsVoteToCard()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("Vote Card"));
        CardAction action = MakeVoteAction("user-1", "approve");

        AdaptiveCard updated = await _state.ActionAsync(card.Id, action);

        updated.Votes.Should().HaveCount(1);
        updated.Votes[0].UserId.Should().Be("user-1");
        updated.Votes[0].Vote.Should().Be("approve");
    }

    [Fact]
    public async Task Vote_SameUserTwice_ReplacesVote()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("Idempotent Vote"));
        await _state.ActionAsync(card.Id, MakeVoteAction("user-1", "approve"));
        AdaptiveCard updated = await _state.ActionAsync(card.Id, MakeVoteAction("user-1", "reject"));

        // Should replace, not duplicate
        updated.Votes.Should().HaveCount(1);
        updated.Votes[0].Vote.Should().Be("reject");
    }

    [Fact]
    public async Task Vote_ChangesLifecycleFromActiveToVoting()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("Lifecycle Vote"));
        card.Lifecycle.Should().Be(CardLifecycle.Active);

        AdaptiveCard updated = await _state.ActionAsync(card.Id, MakeVoteAction("user-1", "approve"));

        updated.Lifecycle.Should().Be(CardLifecycle.Voting);
    }

    [Fact]
    public async Task Vote_ClearMajority_TransitionsToDecided()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("Majority"));
        await _state.ActionAsync(card.Id, MakeVoteAction("user-1", "approve"));
        await _state.ActionAsync(card.Id, MakeVoteAction("user-2", "approve"));
        AdaptiveCard updated = await _state.ActionAsync(card.Id, MakeVoteAction("user-3", "reject"));

        // 2 approve vs 1 reject → clear majority → Decided
        updated.Lifecycle.Should().Be(CardLifecycle.Decided);
    }

    [Fact]
    public async Task Vote_SplitVote_TriggersEscalation()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("Split"));
        await _state.ActionAsync(card.Id, MakeVoteAction("user-1", "approve"));
        AdaptiveCard updated = await _state.ActionAsync(card.Id, MakeVoteAction("user-2", "reject"));

        // 1 approve vs 1 reject → 50/50 split → escalation
        updated.EscalationReason.Should().NotBeNullOrEmpty();
        updated.EscalationReason.Should().Contain("Split vote");
    }

    [Fact]
    public async Task Vote_TallyIsAccurate()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("Tally"));
        await _state.ActionAsync(card.Id, MakeVoteAction("user-1", "approve"));
        await _state.ActionAsync(card.Id, MakeVoteAction("user-2", "approve"));
        await _state.ActionAsync(card.Id, MakeVoteAction("user-3", "reject"));

        AdaptiveCard updated = await _state.GetAsync(card.Id);

        int approves = updated.Votes.Count(v => v.Vote == "approve");
        int rejects = updated.Votes.Count(v => v.Vote == "reject");

        approves.Should().Be(2);
        rejects.Should().Be(1);
    }

    [Fact]
    public async Task Vote_SetsTimestamp()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("Timestamp"));
        AdaptiveCard updated = await _state.ActionAsync(card.Id, MakeVoteAction("user-1", "approve"));

        updated.Votes[0].Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Vote_PreservesUserName()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("UserName"));
        var action = new CardAction("user-1", "Alice Smith", CardActionType.Vote, new() { ["vote"] = "approve" });
        AdaptiveCard updated = await _state.ActionAsync(card.Id, action);

        updated.Votes[0].UserName.Should().Be("Alice Smith");
    }

    [Fact]
    public async Task Vote_DefaultVoteValue_IsApprove()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("Default Vote"));
        // No "vote" key in params → default "approve"
        var action = new CardAction("user-1", "User", CardActionType.Vote, []);
        AdaptiveCard updated = await _state.ActionAsync(card.Id, action);

        updated.Votes[0].Vote.Should().Be("approve");
    }

    [Fact]
    public async Task Vote_SplitDetected_EscalationReasonSet()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("split-detect"));

        // 1st vote
        await _state.ActionAsync(card.Id, MakeVoteAction("u1", "approve"));
        // 2nd vote → split
        AdaptiveCard split = await _state.ActionAsync(card.Id, MakeVoteAction("u2", "reject"));

        split.EscalationReason.Should().NotBeNullOrEmpty("a 50/50 split should trigger escalation");
        split.EscalationReason.Should().Contain("Split vote");
    }

    [Fact]
    public async Task Vote_AfterSplit_ThirdVoteRecorded()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("post-split"));
        await _state.ActionAsync(card.Id, MakeVoteAction("u1", "approve"));
        await _state.ActionAsync(card.Id, MakeVoteAction("u2", "reject"));

        // Third vote should still be recorded
        AdaptiveCard updated = await _state.ActionAsync(card.Id, MakeVoteAction("u3", "approve"));

        updated.Votes.Should().HaveCount(3);
        updated.Votes.Count(v => v.Vote == "approve").Should().Be(2);
        updated.Votes.Count(v => v.Vote == "reject").Should().Be(1);
    }

    [Fact]
    public async Task Vote_OnNonExistentCard_ThrowsKeyNotFound()
    {
        CardAction action = MakeVoteAction("user-1", "approve");
        Func<Task<AdaptiveCard>> act = () => _state.ActionAsync("nonexistent", action);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    #region Helpers

    private static CreateCardRequest MakeRequest(string title)
        => new(title, CardType.Dashboard, "test-user", []);

    private static CardAction MakeVoteAction(string userId, string vote)
        => new(userId, $"User {userId}", CardActionType.Vote, new() { ["vote"] = vote });

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

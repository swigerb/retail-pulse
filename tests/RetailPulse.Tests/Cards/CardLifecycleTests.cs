using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Cards;
using RetailPulse.Api.Hubs;
using RetailPulse.Contracts.Cards;

namespace RetailPulse.Tests.Cards;

/// <summary>
/// Tests for card lifecycle transitions.
/// Covers: full lifecycle, escalation lifecycle, invalid transitions.
/// </summary>
public class CardLifecycleTests
{
    private readonly InMemoryAdaptiveCardState _state;

    public CardLifecycleTests()
    {
        _state = new InMemoryAdaptiveCardState(CreateMockHub(), Mock.Of<ILogger<InMemoryAdaptiveCardState>>());
    }

    [Fact]
    public async Task FullLifecycle_Create_Vote_Decide_Archive()
    {
        // Create
        var card = await _state.CreateAsync(MakeRequest("Full Lifecycle"));
        card.Lifecycle.Should().Be(CardLifecycle.Active);

        // Vote → transitions to Voting
        var after1 = await _state.ActionAsync(card.Id, MakeVoteAction("user-1", "approve"));
        after1.Lifecycle.Should().Be(CardLifecycle.Voting);

        // More votes → clear majority → Decided
        await _state.ActionAsync(card.Id, MakeVoteAction("user-2", "approve"));
        var after3 = await _state.ActionAsync(card.Id, MakeVoteAction("user-3", "reject"));
        after3.Lifecycle.Should().Be(CardLifecycle.Decided);

        // Archive
        await _state.ArchiveAsync(card.Id);
        var final = await _state.GetAsync(card.Id);
        final.Lifecycle.Should().Be(CardLifecycle.Archived);
    }

    [Fact]
    public async Task EscalationLifecycle_Create_Vote_Split_Escalate()
    {
        var card = await _state.CreateAsync(MakeRequest("Escalation Lifecycle"));

        // Vote → split
        await _state.ActionAsync(card.Id, MakeVoteAction("user-1", "approve"));
        var split = await _state.ActionAsync(card.Id, MakeVoteAction("user-2", "reject"));
        split.EscalationReason.Should().Contain("Split vote");

        // Explicit escalation → Decided
        var escalateAction = new CardAction("manager-1", "Manager", CardActionType.Escalate,
            new() { ["reason"] = "Management override" });
        var escalated = await _state.ActionAsync(card.Id, escalateAction);
        escalated.Lifecycle.Should().Be(CardLifecycle.Decided);
        escalated.EscalationReason.Should().Be("Management override");
    }

    [Fact]
    public async Task DrillDown_DoesNotChangeLifecycle()
    {
        var card = await _state.CreateAsync(MakeRequest("DrillDown"));
        var action = new CardAction("user-1", "User 1", CardActionType.DrillDown,
            new() { ["field"] = "revenue" });

        var updated = await _state.ActionAsync(card.Id, action);

        updated.Lifecycle.Should().Be(CardLifecycle.Active);
        updated.Data.Should().ContainKey("drilldown:revenue");
    }

    [Fact]
    public async Task ExplicitEscalation_TransitionsToDecided()
    {
        var card = await _state.CreateAsync(MakeRequest("Explicit Escalate"));
        var action = new CardAction("admin-1", "Admin", CardActionType.Escalate,
            new() { ["reason"] = "Urgent review needed" });

        var updated = await _state.ActionAsync(card.Id, action);

        updated.Lifecycle.Should().Be(CardLifecycle.Decided);
        updated.EscalationReason.Should().Be("Urgent review needed");
    }

    [Fact]
    public async Task VotingTypeCard_StartsInVotingLifecycle()
    {
        var request = new CreateCardRequest("Voting Card", CardType.Voting, "user-1", []);
        var card = await _state.CreateAsync(request);

        card.Lifecycle.Should().Be(CardLifecycle.Voting);
    }

    [Fact]
    public async Task CannotVoteOnArchivedCard()
    {
        var card = await _state.CreateAsync(MakeRequest("Archived Block"));
        await _state.ArchiveAsync(card.Id);

        var act = () => _state.ActionAsync(card.Id, MakeVoteAction("user-1", "approve"));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CannotEscalateArchivedCard()
    {
        var card = await _state.CreateAsync(MakeRequest("Archived Escalate"));
        await _state.ArchiveAsync(card.Id);

        var action = new CardAction("admin", "Admin", CardActionType.Escalate, new() { ["reason"] = "test" });
        var act = () => _state.ActionAsync(card.Id, action);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CannotDrillDownArchivedCard()
    {
        var card = await _state.CreateAsync(MakeRequest("Archived DrillDown"));
        await _state.ArchiveAsync(card.Id);

        var action = new CardAction("user-1", "User", CardActionType.DrillDown, new() { ["field"] = "x" });
        var act = () => _state.ActionAsync(card.Id, action);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ArchiveCanHappenFromAnyNonArchivedState()
    {
        // From Active
        var active = await _state.CreateAsync(MakeRequest("Archive from Active"));
        await _state.ArchiveAsync(active.Id);
        (await _state.GetAsync(active.Id)).Lifecycle.Should().Be(CardLifecycle.Archived);

        // From Voting
        var voting = await _state.CreateAsync(new CreateCardRequest("V", CardType.Voting, "u", []));
        await _state.ArchiveAsync(voting.Id);
        (await _state.GetAsync(voting.Id)).Lifecycle.Should().Be(CardLifecycle.Archived);

        // From Decided (via escalation)
        var decided = await _state.CreateAsync(MakeRequest("D"));
        await _state.ActionAsync(decided.Id, new CardAction("a", "A", CardActionType.Escalate, new() { ["reason"] = "x" }));
        await _state.ArchiveAsync(decided.Id);
        (await _state.GetAsync(decided.Id)).Lifecycle.Should().Be(CardLifecycle.Archived);
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

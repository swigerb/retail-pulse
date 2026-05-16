using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Cards;
using RetailPulse.Api.Hubs;
using RetailPulse.Contracts.Cards;

namespace RetailPulse.Tests.Cards;

/// <summary>
/// Tests for InMemoryAdaptiveCardState CRUD operations and lifecycle basics.
/// Covers: create, get, get non-existent, list active, archive, action on archived.
/// </summary>
public class CardStateTests
{
    private readonly InMemoryAdaptiveCardState _state;

    public CardStateTests()
    {
        IHubContext<TelemetryHub> hub = CreateMockHub();
        _state = new InMemoryAdaptiveCardState(hub, Mock.Of<ILogger<InMemoryAdaptiveCardState>>());
    }

    #region CreateAsync

    [Fact]
    public async Task CreateCard_ReturnsCardWithId()
    {
        CreateCardRequest request = MakeRequest("Test Card");

        AdaptiveCard card = await _state.CreateAsync(request);

        card.Should().NotBeNull();
        card.Id.Should().NotBeNullOrEmpty();
        card.Title.Should().Be("Test Card");
    }

    [Fact]
    public async Task CreateCard_SetsDashboardTypeToActiveLifecycle()
    {
        var request = new CreateCardRequest("Dashboard Card", CardType.Dashboard, "user-1", []);

        AdaptiveCard card = await _state.CreateAsync(request);

        card.Lifecycle.Should().Be(CardLifecycle.Active);
    }

    [Fact]
    public async Task CreateCard_SetsVotingTypeToVotingLifecycle()
    {
        var request = new CreateCardRequest("Voting Card", CardType.Voting, "user-1", []);

        AdaptiveCard card = await _state.CreateAsync(request);

        card.Lifecycle.Should().Be(CardLifecycle.Voting);
    }

    [Fact]
    public async Task CreateCard_SetsCreatedByAndTimestamp()
    {
        CreateCardRequest request = MakeRequest("Meta Card", createdBy: "admin-1");

        AdaptiveCard card = await _state.CreateAsync(request);

        card.CreatedBy.Should().Be("admin-1");
        card.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CreateCard_StartsWithEmptyVotesAndComments()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("Empty Card"));

        card.Votes.Should().BeEmpty();
        card.Comments.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateCard_PreservesDataDictionary()
    {
        var data = new Dictionary<string, object> { ["brand"] = "Apex", ["region"] = "Northeast" };
        var request = new CreateCardRequest("Data Card", CardType.Dashboard, "user-1", data);

        AdaptiveCard card = await _state.CreateAsync(request);

        card.Data.Should().ContainKey("brand").WhoseValue.Should().Be("Apex");
        card.Data.Should().ContainKey("region").WhoseValue.Should().Be("Northeast");
    }

    #endregion

    #region GetAsync

    [Fact]
    public async Task GetCard_ReturnsCorrectCard()
    {
        AdaptiveCard created = await _state.CreateAsync(MakeRequest("Lookup Card"));

        AdaptiveCard fetched = await _state.GetAsync(created.Id);

        fetched.Id.Should().Be(created.Id);
        fetched.Title.Should().Be("Lookup Card");
    }

    [Fact]
    public async Task GetCard_NonExistentId_ThrowsKeyNotFound()
    {
        Func<Task<AdaptiveCard>> act = () => _state.GetAsync("nonexistent-card-id");

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    #endregion

    #region GetActiveAsync

    [Fact]
    public async Task ListActiveCards_FiltersOutArchived()
    {
        AdaptiveCard card1 = await _state.CreateAsync(MakeRequest("Active Card"));
        AdaptiveCard card2 = await _state.CreateAsync(MakeRequest("Archive Me"));

        await _state.ArchiveAsync(card2.Id);

        IReadOnlyList<AdaptiveCard> active = await _state.GetActiveAsync();

        active.Should().HaveCount(1);
        active[0].Id.Should().Be(card1.Id);
    }

    [Fact]
    public async Task ListActiveCards_IncludesAllNonArchivedLifecycles()
    {
        // Create a Dashboard (Active) and a Voting type card (starts as Voting)
        await _state.CreateAsync(new CreateCardRequest("Active", CardType.Dashboard, "u1", []));
        await _state.CreateAsync(new CreateCardRequest("Voting", CardType.Voting, "u1", []));

        IReadOnlyList<AdaptiveCard> active = await _state.GetActiveAsync();

        active.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task ListActiveCards_EmptyStore_ReturnsEmpty()
    {
        IReadOnlyList<AdaptiveCard> active = await _state.GetActiveAsync();
        active.Should().BeEmpty();
    }

    [Fact]
    public async Task ListActiveCards_OrderedByCreatedAtDescending()
    {
        await _state.CreateAsync(MakeRequest("First"));
        await Task.Delay(10); // Ensure different timestamps
        await _state.CreateAsync(MakeRequest("Second"));

        IReadOnlyList<AdaptiveCard> active = await _state.GetActiveAsync();

        active[0].Title.Should().Be("Second");
        active[1].Title.Should().Be("First");
    }

    #endregion

    #region ArchiveAsync

    [Fact]
    public async Task ArchiveCard_ChangesLifecycleToArchived()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("To Archive"));

        await _state.ArchiveAsync(card.Id);

        AdaptiveCard fetched = await _state.GetAsync(card.Id);
        fetched.Lifecycle.Should().Be(CardLifecycle.Archived);
    }

    [Fact]
    public async Task ArchiveCard_NonExistentId_ThrowsKeyNotFound()
    {
        Func<Task> act = () => _state.ArchiveAsync("nonexistent-id");
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    #endregion

    #region Action on Archived

    [Fact]
    public async Task ActionOnArchivedCard_ThrowsInvalidOperation()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("Archived"));
        await _state.ArchiveAsync(card.Id);

        var action = new CardAction("user-1", "User 1", CardActionType.Vote, new() { ["vote"] = "approve" });
        Func<Task<AdaptiveCard>> act = () => _state.ActionAsync(card.Id, action);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*archived*");
    }

    [Fact]
    public async Task CommentOnArchivedCard_ThrowsInvalidOperation()
    {
        AdaptiveCard card = await _state.CreateAsync(MakeRequest("Archived"));
        await _state.ArchiveAsync(card.Id);

        var action = new CardAction("user-1", "User 1", CardActionType.Comment, new() { ["text"] = "Hello" });
        Func<Task<AdaptiveCard>> act = () => _state.ActionAsync(card.Id, action);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    #endregion

    #region Helpers

    private static CreateCardRequest MakeRequest(string title, string createdBy = "user-1")
        => new(title, CardType.Dashboard, createdBy, []);

    private static IHubContext<TelemetryHub> CreateMockHub()
    {
        var mockClients = new Mock<IHubClients>();
        var mockProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.All).Returns(mockProxy.Object);
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockProxy.Object);

        var mockHub = new Mock<IHubContext<TelemetryHub>>();
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
        return mockHub.Object;
    }

    #endregion
}

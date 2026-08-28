using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Cards;
using RetailPulse.Api.Hubs;
using RetailPulse.Contracts.Cards;
using RetailPulse.Contracts.Consensus;

namespace RetailPulse.Tests.Cards;

/// <summary>
/// Pins the council-verdict-to-card publication path.
/// </summary>
/// <remarks>
/// <c>CreateFromVerdictAsync</c> was fully implemented but had zero callers, so the
/// documented demo step — "a Collaborative Adaptive Card is auto-created with the
/// council's verdict" — never happened and the Cards panel was permanently empty.
/// These tests assert the projection itself is faithful, so a future refactor cannot
/// quietly drop the agent attribution or the verdict context again.
/// </remarks>
public class CouncilVerdictCardTests
{
    private readonly InMemoryAdaptiveCardState _state;

    public CouncilVerdictCardTests()
    {
        _state = new InMemoryAdaptiveCardState(CreateMockHub(), Mock.Of<ILogger<InMemoryAdaptiveCardState>>());
    }

    private static CouncilVerdict BuildVerdict() => new(
        Brand: "Apex Grill",
        Region: "Northeast",
        OverallRating: HealthRating.Red,
        Synthesis: "Supply reliability is dragging the brand into the red.",
        Votes:
        [
            new AgentVote("demand-forecasting", "Demand Forecasting", HealthRating.Yellow, "Demand holding", 0.86, [], TimeSpan.FromSeconds(4)),
            new AgentVote("supply-chain", "Supply Chain", HealthRating.Red, "Two anchors out of stock", 0.93, [], TimeSpan.FromSeconds(6)),
        ],
        IsUnanimous: false,
        Disagreements: ["demand-forecasting reads the trend as recoverable"],
        ActionItems: ["Expedite replenishment on the two out-of-stock anchors"],
        ConvenedAt: DateTime.UtcNow,
        TotalDuration: TimeSpan.FromSeconds(11));

    [Fact]
    public async Task CreateFromVerdict_PublishesADiscoverableVotingCard()
    {
        AdaptiveCard card = await _state.CreateFromVerdictAsync(BuildVerdict());

        card.Type.Should().Be(CardType.Voting);

        // The whole point is that the Cards panel, which lists active cards, can find it.
        IReadOnlyList<AdaptiveCard> active = await _state.GetActiveAsync();
        active.Should().ContainSingle(c => c.Id == card.Id);
    }

    [Fact]
    public async Task CreateFromVerdict_CarriesTheBrandAndRatingInTheTitle()
    {
        AdaptiveCard card = await _state.CreateFromVerdictAsync(BuildVerdict());

        card.Title.Should().Contain("Apex Grill").And.Contain("Red");
    }

    [Fact]
    public async Task CreateFromVerdict_MapsEveryAgentVoteWithItsAttribution()
    {
        AdaptiveCard card = await _state.CreateFromVerdictAsync(BuildVerdict());

        card.Votes.Should().HaveCount(2);
        card.Votes.Should().Contain(v => v.UserId == "supply-chain" && v.Vote == "Red");
        card.Votes.Should().Contain(v => v.UserId == "demand-forecasting" && v.Vote == "Yellow");
    }

    [Fact]
    public async Task CreateFromVerdict_PreservesTheVerdictContextForTheCardBody()
    {
        AdaptiveCard card = await _state.CreateFromVerdictAsync(BuildVerdict());

        card.Data["brand"].ToString().Should().Be("Apex Grill");
        card.Data["region"].ToString().Should().Be("Northeast");
        card.Data["overall_rating"].ToString().Should().Be("Red");
        card.Data["synthesis"].ToString().Should().Contain("Supply reliability");
    }

    [Fact]
    public async Task CreateFromVerdict_RecordsEachConveneSeparately()
    {
        await _state.CreateFromVerdictAsync(BuildVerdict());
        await _state.CreateFromVerdictAsync(BuildVerdict());

        IReadOnlyList<AdaptiveCard> active = await _state.GetActiveAsync();
        active.Should().HaveCount(2, "each council run is its own decision to collaborate on");
    }

    private static IHubContext<TelemetryHub> CreateMockHub()
    {
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.All).Returns(Mock.Of<IClientProxy>());
        var hub = new Mock<IHubContext<TelemetryHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        return hub.Object;
    }
}

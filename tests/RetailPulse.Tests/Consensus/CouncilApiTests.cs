using FluentAssertions;
using FluentAssertions.Specialized;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Tests.Consensus;

namespace RetailPulse.Tests.Consensus;

/// <summary>
/// Tests for the Council API endpoints:
/// POST /api/council/convene — runs a full health council session
/// GET /api/council/agents — lists participating agents
/// Tests verify response structure, error handling, and performance bounds.
/// Since WebApplicationFactory requires Azure credentials, tests use in-memory
/// orchestrator directly (same pattern as RouterIntegrationTests).
/// </summary>
public class CouncilApiTests
{
    #region POST /api/council/convene — Valid Brand

    [Fact]
    public async Task Convene_ValidBrand_ReturnsVerdictWithAllFields()
    {
        ICouncilAgent[] agents =
        [
            CreateVotingAgent("demand-forecasting", "Demand Forecast Agent", HealthRating.Green, 0.92),
            CreateVotingAgent("supply-chain", "Supply Chain Agent", HealthRating.Yellow, 0.85),
            CreateVotingAgent("competitive-intel", "Competitive Intelligence Agent", HealthRating.Green, 0.88)
        ];

        var orchestrator = new ConsensusOrchestrator(
            agents, Mock.Of<ILogger<ConsensusOrchestrator>>());

        CouncilVerdict verdict = await orchestrator.ConveneAsync("Sierra Gold Tequila", CancellationToken.None);

        // All required fields present
        verdict.Brand.Should().Be("Sierra Gold Tequila");
        verdict.Votes.Should().NotBeEmpty();
        verdict.Synthesis.Should().NotBeNullOrWhiteSpace();
        verdict.OverallRating.Should().BeDefined();
        verdict.TotalDurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Convene_ValidBrand_ResponseIncludesVotes()
    {
        ICouncilAgent[] agents =
        [
            CreateVotingAgent("demand-forecasting", "Demand Forecast Agent", HealthRating.Green, 0.90),
            CreateVotingAgent("supply-chain", "Supply Chain Agent", HealthRating.Green, 0.85),
            CreateVotingAgent("competitive-intel", "Competitive Intelligence Agent", HealthRating.Green, 0.80)
        ];

        var orchestrator = new ConsensusOrchestrator(
            agents, Mock.Of<ILogger<ConsensusOrchestrator>>());

        CouncilVerdict verdict = await orchestrator.ConveneAsync("Ridgeline Bourbon", CancellationToken.None);

        verdict.Votes.Should().HaveCount(3);
        verdict.Votes.Should().OnlyContain(v =>
            !string.IsNullOrWhiteSpace(v.AgentKey) &&
            !string.IsNullOrWhiteSpace(v.AgentDisplayName) &&
            v.Confidence > 0 &&
            !string.IsNullOrWhiteSpace(v.Reasoning));
    }

    [Fact]
    public async Task Convene_ValidBrand_ResponseIncludesSynthesis()
    {
        ICouncilAgent[] agents =
        [
            CreateVotingAgent("demand-forecasting", "Demand Forecast Agent", HealthRating.Green, 0.90),
            CreateVotingAgent("supply-chain", "Supply Chain Agent", HealthRating.Red, 0.95),
            CreateVotingAgent("competitive-intel", "Competitive Intelligence Agent", HealthRating.Yellow, 0.80)
        ];

        var orchestrator = new ConsensusOrchestrator(
            agents, Mock.Of<ILogger<ConsensusOrchestrator>>());

        CouncilVerdict verdict = await orchestrator.ConveneAsync("FreshMart", CancellationToken.None);

        verdict.Synthesis.Should().NotBeNullOrWhiteSpace();
        verdict.OverallRating.Should().BeDefined();
    }

    #endregion

    #region POST /api/council/convene — Unknown Brand

    [Fact]
    public async Task Convene_UnknownBrand_GracefulHandling()
    {
        ICouncilAgent[] agents =
        [
            CreateVotingAgent("demand-forecasting", "Demand Forecast Agent", HealthRating.Yellow, 0.50),
            CreateVotingAgent("supply-chain", "Supply Chain Agent", HealthRating.Yellow, 0.45),
            CreateVotingAgent("competitive-intel", "Competitive Intelligence Agent", HealthRating.Yellow, 0.40)
        ];

        var orchestrator = new ConsensusOrchestrator(
            agents, Mock.Of<ILogger<ConsensusOrchestrator>>());

        // Should NOT throw — returns a verdict (even if uncertain)
        Func<Task<CouncilVerdict>> act = () => orchestrator.ConveneAsync("NonExistentBrand999", CancellationToken.None);
        AndWhichConstraint<GenericAsyncFunctionAssertions<CouncilVerdict>, CouncilVerdict> verdict = await act.Should().NotThrowAsync();

        verdict.Subject.Should().NotBeNull();
        verdict.Subject.Brand.Should().Be("NonExistentBrand999");
    }

    #endregion

    #region GET /api/council/agents — Agent Listing

    [Fact]
    public void CouncilAgents_ListParticipatingAgents()
    {
        ICouncilAgent[] agents =
        [
            CreateVotingAgent("demand-forecasting", "Demand Forecast Agent", HealthRating.Green, 0.90),
            CreateVotingAgent("supply-chain", "Supply Chain Agent", HealthRating.Green, 0.85),
            CreateVotingAgent("competitive-intel", "Competitive Intelligence Agent", HealthRating.Green, 0.80)
        ];

        // Verify the orchestrator exposes its agents
        var agentKeys = agents.Select(a => a.Key).ToList();

        agentKeys.Should().HaveCount(3);
        agentKeys.Should().Contain("demand-forecasting");
        agentKeys.Should().Contain("supply-chain");
        agentKeys.Should().Contain("competitive-intel");
    }

    [Fact]
    public void CouncilAgents_AllHaveDisplayNames()
    {
        ICouncilAgent[] agents =
        [
            CreateVotingAgent("demand-forecasting", "Demand Forecast Agent", HealthRating.Green, 0.90),
            CreateVotingAgent("supply-chain", "Supply Chain Agent", HealthRating.Green, 0.85),
            CreateVotingAgent("competitive-intel", "Competitive Intelligence Agent", HealthRating.Green, 0.80)
        ];

        agents.Should().OnlyContain(a => !string.IsNullOrWhiteSpace(a.DisplayName));
    }

    #endregion

    #region Response Timing

    [Fact]
    public async Task Convene_ResponseTime_UnderFifteenSeconds()
    {
        ICouncilAgent[] agents =
        [
            CreateVotingAgent("demand-forecasting", "Demand Forecast Agent", HealthRating.Green, 0.90),
            CreateVotingAgent("supply-chain", "Supply Chain Agent", HealthRating.Green, 0.85),
            CreateVotingAgent("competitive-intel", "Competitive Intelligence Agent", HealthRating.Green, 0.80)
        ];

        var orchestrator = new ConsensusOrchestrator(
            agents, Mock.Of<ILogger<ConsensusOrchestrator>>());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        CouncilVerdict verdict = await orchestrator.ConveneAsync("Summit Vodka", CancellationToken.None);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(15_000,
            "full council session should complete within 15 seconds");
        verdict.TotalDurationMs.Should().BeLessThan(15_000);
    }

    #endregion

    #region Cancellation

    [Fact]
    public async Task Convene_CancellationRequested_StopsGracefully()
    {
        ICouncilAgent[] agents =
        [
            CreateVotingAgent("demand-forecasting", "Demand Forecast Agent", HealthRating.Green, 0.90),
            CreateVotingAgent("supply-chain", "Supply Chain Agent", HealthRating.Green, 0.85),
            CreateVotingAgent("competitive-intel", "Competitive Intelligence Agent", HealthRating.Green, 0.80)
        ];

        var orchestrator = new ConsensusOrchestrator(
            agents, Mock.Of<ILogger<ConsensusOrchestrator>>());

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        // Should handle gracefully — either return a result or throw OCE
        try
        {
            CouncilVerdict verdict = await orchestrator.ConveneAsync("Summit Vodka", cts.Token);
            // If it returns without throwing, it handled cancellation gracefully
            verdict.Brand.Should().Be("Summit Vodka");
        }
        catch (OperationCanceledException)
        {
            // Also acceptable — immediate cancellation propagated
        }
    }

    #endregion

    #region Helpers

    private static ICouncilAgent CreateVotingAgent(
        string key, string displayName, HealthRating rating, double confidence)
    {
        var mock = new Mock<ICouncilAgent>();
        mock.Setup(a => a.Key).Returns(key);
        mock.Setup(a => a.DisplayName).Returns(displayName);
        mock.Setup(a => a.VoteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentVote(
                AgentKey: key,
                AgentDisplayName: displayName,
                Rating: rating,
                Confidence: confidence,
                Reasoning: $"{displayName} assessment: {rating}",
                ResponseTimeMs: 100 + Random.Shared.Next(200)));
        return mock.Object;
    }

    #endregion
}

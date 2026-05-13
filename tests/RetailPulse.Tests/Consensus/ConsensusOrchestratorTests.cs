using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Tests.Consensus;

/// <summary>
/// Tests for ConsensusOrchestrator — the Portfolio Health Council pattern.
/// Fans out to N specialist agents in parallel, collects votes with confidence
/// scores, and synthesizes a CouncilVerdict with disagreement analysis.
/// Test-first: defines the expected contract before implementation exists.
/// </summary>
public class ConsensusOrchestratorTests
{
    #region Unanimous Verdict (All Green)

    [Fact]
    public async Task Convene_AllAgentsVoteGreen_ReturnsUnanimousVerdict()
    {
        var agents = new[]
        {
            CreateVotingAgent("demand-forecasting", "Demand Forecast Agent", HealthRating.Green, 0.95),
            CreateVotingAgent("supply-chain", "Supply Chain Agent", HealthRating.Green, 0.90),
            CreateVotingAgent("competitive-intel", "Competitive Intelligence Agent", HealthRating.Green, 0.88)
        };

        var orchestrator = CreateOrchestrator(agents);

        var verdict = await orchestrator.ConveneAsync("Sierra Gold Tequila", CancellationToken.None);

        verdict.Should().NotBeNull();
        verdict.IsUnanimous.Should().BeTrue("all agents voted Green");
        verdict.OverallRating.Should().Be(HealthRating.Green);
        verdict.Votes.Should().HaveCount(3);
    }

    [Fact]
    public async Task Convene_AllGreen_SynthesisIsBrief()
    {
        var agents = new[]
        {
            CreateVotingAgent("demand-forecasting", "Demand Forecast Agent", HealthRating.Green, 0.95),
            CreateVotingAgent("supply-chain", "Supply Chain Agent", HealthRating.Green, 0.90),
            CreateVotingAgent("competitive-intel", "Competitive Intelligence Agent", HealthRating.Green, 0.88)
        };

        var orchestrator = CreateOrchestrator(agents);
        var verdict = await orchestrator.ConveneAsync("Sierra Gold Tequila", CancellationToken.None);

        verdict.Synthesis.Should().NotBeNullOrWhiteSpace();
        // Unanimous verdicts should be concise
        verdict.Disagreements.Should().BeEmpty("no disagreements when unanimous");
    }

    #endregion

    #region Split Vote (2 Green, 1 Red)

    [Fact]
    public async Task Convene_SplitVote_ReturnsNonUnanimous()
    {
        var agents = new[]
        {
            CreateVotingAgent("demand-forecasting", "Demand Forecast Agent", HealthRating.Green, 0.85),
            CreateVotingAgent("supply-chain", "Supply Chain Agent", HealthRating.Red, 0.92),
            CreateVotingAgent("competitive-intel", "Competitive Intelligence Agent", HealthRating.Green, 0.80)
        };

        var orchestrator = CreateOrchestrator(agents);
        var verdict = await orchestrator.ConveneAsync("Ridgeline Bourbon", CancellationToken.None);

        verdict.IsUnanimous.Should().BeFalse("one agent disagrees");
        verdict.Disagreements.Should().NotBeEmpty("should explain the supply chain concern");
    }

    [Fact]
    public async Task Convene_SplitVote_OverallReflectsMajority()
    {
        var agents = new[]
        {
            CreateVotingAgent("demand-forecasting", "Demand Forecast Agent", HealthRating.Green, 0.85),
            CreateVotingAgent("supply-chain", "Supply Chain Agent", HealthRating.Red, 0.70),
            CreateVotingAgent("competitive-intel", "Competitive Intelligence Agent", HealthRating.Green, 0.80)
        };

        var orchestrator = CreateOrchestrator(agents);
        var verdict = await orchestrator.ConveneAsync("Ridgeline Bourbon", CancellationToken.None);

        // With 2 Green and 1 Red, overall should not be Green (Red dissent is serious)
        verdict.OverallRating.Should().NotBe(HealthRating.Green,
            "a Red vote from any agent should prevent an all-clear");
    }

    #endregion

    #region All Different Votes (Green, Yellow, Red)

    [Fact]
    public async Task Convene_AllDifferentVotes_StrongDisagreement()
    {
        var agents = new[]
        {
            CreateVotingAgent("demand-forecasting", "Demand Forecast Agent", HealthRating.Green, 0.80),
            CreateVotingAgent("supply-chain", "Supply Chain Agent", HealthRating.Yellow, 0.75),
            CreateVotingAgent("competitive-intel", "Competitive Intelligence Agent", HealthRating.Red, 0.90)
        };

        var orchestrator = CreateOrchestrator(agents);
        var verdict = await orchestrator.ConveneAsync("Summit Vodka", CancellationToken.None);

        verdict.IsUnanimous.Should().BeFalse();
        verdict.Disagreements.Should().HaveCountGreaterThanOrEqualTo(2,
            "three different votes means at least two disagreement pairs");
    }

    [Fact]
    public async Task Convene_AllDifferentVotes_DetailedSynthesis()
    {
        var agents = new[]
        {
            CreateVotingAgent("demand-forecasting", "Demand Forecast Agent", HealthRating.Green, 0.80),
            CreateVotingAgent("supply-chain", "Supply Chain Agent", HealthRating.Yellow, 0.75),
            CreateVotingAgent("competitive-intel", "Competitive Intelligence Agent", HealthRating.Red, 0.90)
        };

        var orchestrator = CreateOrchestrator(agents);
        var verdict = await orchestrator.ConveneAsync("Summit Vodka", CancellationToken.None);

        verdict.Synthesis.Should().NotBeNullOrWhiteSpace();
        verdict.Synthesis.Length.Should().BeGreaterThan(50,
            "strong disagreement should produce a detailed synthesis");
    }

    #endregion

    #region Timeout Handling

    [Fact]
    public async Task Convene_OneAgentTimesOut_ReturnsPartialVerdict()
    {
        var fastAgent1 = CreateVotingAgent("demand-forecasting", "Demand Forecast Agent", HealthRating.Green, 0.90);
        var fastAgent2 = CreateVotingAgent("competitive-intel", "Competitive Intelligence Agent", HealthRating.Yellow, 0.85);
        var slowAgent = CreateTimingOutAgent("supply-chain", "Supply Chain Agent");

        var agents = new[] { fastAgent1, slowAgent, fastAgent2 };
        var orchestrator = CreateOrchestrator(agents, timeoutMs: 2000);

        var verdict = await orchestrator.ConveneAsync("FreshMart", CancellationToken.None);

        verdict.Should().NotBeNull("should return a verdict even with partial results");
        verdict.Votes.Count.Should().BeGreaterThanOrEqualTo(2,
            "at least the non-timed-out agents should have votes");
        verdict.TimedOutAgents.Should().Contain("supply-chain");
    }

    [Fact]
    public async Task Convene_AllAgentsTimeout_GracefulFailure()
    {
        var agents = new[]
        {
            CreateTimingOutAgent("demand-forecasting", "Demand Forecast Agent"),
            CreateTimingOutAgent("supply-chain", "Supply Chain Agent"),
            CreateTimingOutAgent("competitive-intel", "Competitive Intelligence Agent")
        };

        var orchestrator = CreateOrchestrator(agents, timeoutMs: 1000);

        // Should NOT throw — returns a failure verdict
        var verdict = await orchestrator.ConveneAsync("Apex Grill", CancellationToken.None);

        verdict.Should().NotBeNull("should not crash on total timeout");
        verdict.Votes.Should().BeEmpty("no agents responded in time");
        verdict.TimedOutAgents.Should().HaveCount(3);
    }

    #endregion

    #region Brand Propagation

    [Fact]
    public async Task Convene_BrandIsPassedToAllAgents()
    {
        var capturedBrands = new List<string>();

        var agents = new[]
        {
            CreateBrandCapturingAgent("demand-forecasting", capturedBrands, HealthRating.Green),
            CreateBrandCapturingAgent("supply-chain", capturedBrands, HealthRating.Green),
            CreateBrandCapturingAgent("competitive-intel", capturedBrands, HealthRating.Green)
        };

        var orchestrator = CreateOrchestrator(agents);
        await orchestrator.ConveneAsync("Sierra Gold Tequila", CancellationToken.None);

        capturedBrands.Should().HaveCount(3);
        capturedBrands.Should().OnlyContain(b => b == "Sierra Gold Tequila",
            "every agent must receive the exact brand name");
    }

    #endregion

    #region Vote Structure

    [Fact]
    public async Task Convene_VotesIncludeConfidenceScores()
    {
        var agents = new[]
        {
            CreateVotingAgent("demand-forecasting", "Demand Forecast Agent", HealthRating.Green, 0.95),
            CreateVotingAgent("supply-chain", "Supply Chain Agent", HealthRating.Yellow, 0.72),
            CreateVotingAgent("competitive-intel", "Competitive Intelligence Agent", HealthRating.Green, 0.88)
        };

        var orchestrator = CreateOrchestrator(agents);
        var verdict = await orchestrator.ConveneAsync("Ridgeline Bourbon", CancellationToken.None);

        verdict.Votes.Should().OnlyContain(v => v.Confidence > 0.0 && v.Confidence <= 1.0,
            "every vote must have a valid confidence score between 0 and 1");
    }

    [Fact]
    public async Task Convene_ResponseTimesTrackedPerAgent()
    {
        var agents = new[]
        {
            CreateVotingAgent("demand-forecasting", "Demand Forecast Agent", HealthRating.Green, 0.90),
            CreateVotingAgent("supply-chain", "Supply Chain Agent", HealthRating.Green, 0.85),
            CreateVotingAgent("competitive-intel", "Competitive Intelligence Agent", HealthRating.Green, 0.80)
        };

        var orchestrator = CreateOrchestrator(agents);
        var verdict = await orchestrator.ConveneAsync("Summit Vodka", CancellationToken.None);

        verdict.Votes.Should().OnlyContain(v => v.ResponseTimeMs >= 0,
            "every vote should track response time");
    }

    [Fact]
    public async Task Convene_TotalDurationTracked()
    {
        var agents = new[]
        {
            CreateVotingAgent("demand-forecasting", "Demand Forecast Agent", HealthRating.Green, 0.90),
            CreateVotingAgent("supply-chain", "Supply Chain Agent", HealthRating.Green, 0.85),
            CreateVotingAgent("competitive-intel", "Competitive Intelligence Agent", HealthRating.Green, 0.80)
        };

        var orchestrator = CreateOrchestrator(agents);
        var verdict = await orchestrator.ConveneAsync("Summit Vodka", CancellationToken.None);

        verdict.TotalDurationMs.Should().BeGreaterThanOrEqualTo(0,
            "total council duration must be tracked");
    }

    #endregion

    #region Action Items

    [Fact]
    public async Task Convene_RedVote_GeneratesActionItems()
    {
        var agents = new[]
        {
            CreateVotingAgent("demand-forecasting", "Demand Forecast Agent", HealthRating.Green, 0.90),
            CreateVotingAgent("supply-chain", "Supply Chain Agent", HealthRating.Red, 0.95,
                reasoning: "Critical supply disruption in Northeast affecting fulfillment"),
            CreateVotingAgent("competitive-intel", "Competitive Intelligence Agent", HealthRating.Green, 0.80)
        };

        var orchestrator = CreateOrchestrator(agents);
        var verdict = await orchestrator.ConveneAsync("Harvest Table", CancellationToken.None);

        verdict.ActionItems.Should().NotBeEmpty(
            "a Red vote should generate at least one action item");
    }

    #endregion

    #region Helpers

    private static IConsensusCouncil CreateOrchestrator(
        ICouncilAgent[] agents,
        int timeoutMs = 30000)
    {
        return new ConsensusOrchestrator(
            agents,
            Mock.Of<ILogger<ConsensusOrchestrator>>(),
            TimeSpan.FromMilliseconds(timeoutMs));
    }

    private static ICouncilAgent CreateVotingAgent(
        string key,
        string displayName,
        HealthRating rating,
        double confidence,
        string? reasoning = null)
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
                Reasoning: reasoning ?? $"{displayName} assessment: {rating}",
                ResponseTimeMs: 150 + Random.Shared.Next(500)));
        return mock.Object;
    }

    private static ICouncilAgent CreateTimingOutAgent(string key, string displayName)
    {
        var mock = new Mock<ICouncilAgent>();
        mock.Setup(a => a.Key).Returns(key);
        mock.Setup(a => a.DisplayName).Returns(displayName);
        mock.Setup(a => a.VoteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
                throw new OperationCanceledException("Timed out");
            });
        return mock.Object;
    }

    private static ICouncilAgent CreateBrandCapturingAgent(
        string key,
        List<string> capturedBrands,
        HealthRating rating)
    {
        var mock = new Mock<ICouncilAgent>();
        mock.Setup(a => a.Key).Returns(key);
        mock.Setup(a => a.DisplayName).Returns(key);
        mock.Setup(a => a.VoteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string brand, CancellationToken _) =>
            {
                capturedBrands.Add(brand);
                return Task.FromResult(new AgentVote(key, key, rating, 0.9,
                    $"Assessment for {brand}", 100));
            });
        return mock.Object;
    }

    #endregion
}

// ── Contract types (will live in Contracts project once implemented) ──────────

/// <summary>Health rating for a brand domain — traffic-light model.</summary>
public enum HealthRating { Green, Yellow, Red }

/// <summary>A single agent's vote in a council session.</summary>
public record AgentVote(
    string AgentKey,
    string AgentDisplayName,
    HealthRating Rating,
    double Confidence,
    string Reasoning,
    long ResponseTimeMs);

/// <summary>
/// The synthesized output of a full council convene session.
/// </summary>
public record CouncilVerdict(
    string Brand,
    HealthRating OverallRating,
    bool IsUnanimous,
    List<AgentVote> Votes,
    string Synthesis,
    List<string> Disagreements,
    List<string> ActionItems,
    List<string> TimedOutAgents,
    long TotalDurationMs);

/// <summary>
/// Interface for agents that can participate in a health council vote.
/// </summary>
public interface ICouncilAgent
{
    string Key { get; }
    string DisplayName { get; }
    Task<AgentVote> VoteAsync(string brand, CancellationToken ct = default);
}

/// <summary>
/// Orchestrates a portfolio health council — fans out to agents, collects votes,
/// synthesizes a verdict.
/// </summary>
public interface IConsensusCouncil
{
    Task<CouncilVerdict> ConveneAsync(string brand, CancellationToken ct = default);
}

/// <summary>
/// Concrete orchestrator — will be implemented in Api project.
/// Defined here as a stub so tests compile and define the expected contract.
/// </summary>
public class ConsensusOrchestrator : IConsensusCouncil
{
    private readonly ICouncilAgent[] _agents;
    private readonly ILogger<ConsensusOrchestrator> _logger;
    private readonly TimeSpan _timeout;

    public ConsensusOrchestrator(
        ICouncilAgent[] agents,
        ILogger<ConsensusOrchestrator> logger,
        TimeSpan? timeout = null)
    {
        _agents = agents;
        _logger = logger;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<CouncilVerdict> ConveneAsync(string brand, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var votes = new List<AgentVote>();
        var timedOut = new List<string>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);

        var tasks = _agents.Select(async agent =>
        {
            try
            {
                var agentSw = System.Diagnostics.Stopwatch.StartNew();
                var vote = await agent.VoteAsync(brand, cts.Token);
                return (Vote: (AgentVote?)vote, TimedOut: false, Key: agent.Key);
            }
            catch (OperationCanceledException)
            {
                return (Vote: (AgentVote?)null, TimedOut: true, Key: agent.Key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent {AgentKey} failed during council vote", agent.Key);
                return (Vote: (AgentVote?)null, TimedOut: true, Key: agent.Key);
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);

        foreach (var r in results)
        {
            if (r.Vote != null)
                votes.Add(r.Vote);
            if (r.TimedOut)
                timedOut.Add(r.Key);
        }

        var disagreements = new List<string>();
        var actionItems = new List<string>();

        if (votes.Count == 0)
        {
            return new CouncilVerdict(brand, HealthRating.Red, false, votes,
                "Council could not reach a verdict — all agents timed out.",
                disagreements, actionItems, timedOut, sw.ElapsedMilliseconds);
        }

        var ratings = votes.Select(v => v.Rating).Distinct().ToList();
        var isUnanimous = ratings.Count == 1;

        // Determine overall rating
        HealthRating overall;
        if (isUnanimous)
        {
            overall = ratings[0];
        }
        else if (votes.Any(v => v.Rating == HealthRating.Red))
        {
            overall = HealthRating.Yellow; // Red dissent prevents Green
        }
        else
        {
            overall = HealthRating.Yellow;
        }

        // Build disagreements
        if (!isUnanimous)
        {
            var grouped = votes.GroupBy(v => v.Rating).ToList();
            foreach (var g1 in grouped)
            foreach (var g2 in grouped.Where(g => g.Key != g1.Key))
            {
                var pair = $"{g1.First().AgentDisplayName} ({g1.Key}) vs {g2.First().AgentDisplayName} ({g2.Key})";
                if (!disagreements.Contains(pair))
                    disagreements.Add(pair);
            }
        }

        // Generate action items for Red votes
        foreach (var redVote in votes.Where(v => v.Rating == HealthRating.Red))
        {
            actionItems.Add($"[{redVote.AgentDisplayName}] {redVote.Reasoning}");
        }

        // Synthesis
        var synthesis = isUnanimous
            ? $"All agents agree: {brand} health is {overall}."
            : $"Mixed assessment for {brand}: {string.Join(", ", votes.Select(v => $"{v.AgentDisplayName}={v.Rating}"))}. " +
              $"Key concerns: {string.Join("; ", votes.Where(v => v.Rating != HealthRating.Green).Select(v => v.Reasoning))}";

        return new CouncilVerdict(brand, overall, isUnanimous, votes,
            synthesis, disagreements, actionItems, timedOut, sw.ElapsedMilliseconds);
    }
}

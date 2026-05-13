namespace RetailPulse.Contracts.Consensus;

/// <summary>
/// Health rating for a brand across a specialist domain.
/// </summary>
public enum HealthRating { Green, Yellow, Red }

/// <summary>
/// A single specialist agent's health assessment vote.
/// </summary>
public record AgentVote(
    string AgentId,
    string AgentName,
    HealthRating Rating,
    string Reasoning,
    double Confidence,
    string[] KeyMetrics,
    TimeSpan ResponseTime
);

/// <summary>
/// The aggregated verdict from the Portfolio Health Council
/// after all specialist agents have voted and the synthesizer
/// has resolved disagreements.
/// </summary>
public record CouncilVerdict(
    string Brand,
    string? Region,
    HealthRating OverallRating,
    string Synthesis,
    AgentVote[] Votes,
    bool IsUnanimous,
    string[] Disagreements,
    string[] ActionItems,
    DateTime ConvenedAt,
    TimeSpan TotalDuration
);

/// <summary>
/// Orchestrates the Portfolio Health Council — fans out health
/// assessment requests to multiple specialist agents in parallel,
/// collects votes, and synthesizes a unified verdict.
/// </summary>
public interface IConsensusCouncil
{
    /// <summary>
    /// Convene the council for a brand health assessment.
    /// </summary>
    Task<CouncilVerdict> ConveneAsync(string brand, string? region, CancellationToken ct = default);
}

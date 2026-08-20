namespace RetailPulse.Api.Scorecard;

/// <summary>
/// Configuration entry for a single scorecard dimension. Sourced from each specialist's
/// <c>AgentDefinition.ScorecardDimension</c> / <c>ScorecardWeight</c> in <c>prompts.yaml</c>
/// so adding a scoring dimension is a config change, not a code change (issue #98).
/// </summary>
/// <param name="Dimension">Display name of the dimension (e.g., "Demand Momentum").</param>
/// <param name="Weight">Weight applied to this dimension's score (0–1).</param>
/// <param name="AgentKey">Key of the specialist agent that produces this dimension's score.</param>
public sealed record ScorecardDimensionConfig(
    string Dimension,
    double Weight,
    string AgentKey);

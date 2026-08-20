namespace RetailPulse.Api.Agents.Routing;

/// <summary>
/// Configuration entry for a router intent that is NOT owned by a specialist agent —
/// typically an in-process orchestrator (e.g., the Portfolio Health Council on
/// <c>council/health</c>, the Scorecard synthesizer on <c>scorecard/portfolio</c>).
/// Lets the data-driven router still carry keyword fast-paths for these paths, so
/// deleting the ConsensusOrchestrator does not silently break intent detection.
/// </summary>
/// <param name="Intent">The router intent value (e.g., <c>council/health</c>).</param>
/// <param name="KeywordFastPaths">Case-insensitive substrings that trigger this intent.</param>
public sealed record RouterIntentConfig(
    string Intent,
    IReadOnlyList<string> KeywordFastPaths);

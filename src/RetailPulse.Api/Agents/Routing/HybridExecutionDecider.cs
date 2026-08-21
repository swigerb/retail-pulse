using RetailPulse.Api.Persistence;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Agents.Routing;

/// <summary>
/// Pure, allocation-lean decision layer that classifies a routed request as
/// <see cref="ExecutionPath.Fast"/>, <see cref="ExecutionPath.Plan"/>, or
/// <see cref="ExecutionPath.Council"/> (issue #95). Kept as a static helper
/// with no dependencies on the endpoint host so decision-table tests can
/// exercise the full precedence without spinning up ASP.NET Core.
///
/// Precedence — matches the design gate:
/// <list type="number">
///   <item>Explicit user override (only when the caller is authenticated and
///     the requested path is actually available; anonymous or
///     unavailable-planner overrides are ignored, preserving default safety
///     behaviour).</item>
///   <item>Council dedicated route — the portfolio-health intent has its
///     own trigger and always resolves to <see cref="ExecutionPath.Council"/>
///     when no valid override wins.</item>
///   <item>Planner unavailable (anonymous, plan orchestrator not
///     registered, no detected intents) → fast; the plan path is not a
///     reachable destination for that request.</item>
///   <item>Multi-domain — detected intents at or above
///     <see cref="PlanPersistenceOptions.MinDetectedIntentsForPlan"/>
///     → plan.</item>
///   <item>Low confidence — router confidence strictly below
///     <see cref="PlanPersistenceOptions.MinConfidenceForFastPath"/>
///     → plan.</item>
///   <item>Advisory / diagnostic phrase (configured) → plan.</item>
///   <item>Else → fast.</item>
/// </list>
///
/// The gate is on the hot path for every /api/chat turn. It avoids
/// per-call allocations (no LINQ, no lowercasing the input, no list
/// materialisation) so it does not measurably move the fast-path baseline
/// captured for this issue.
/// </summary>
public static class HybridExecutionDecider
{
    /// <summary>
    /// Compute the execution decision for a routed chat turn.
    /// </summary>
    /// <param name="decision">Router classification. Must be non-null.</param>
    /// <param name="message">Original user message; scanned for advisory cues. Must be non-null.</param>
    /// <param name="context">Endpoint-side signals (auth, planner availability, override).</param>
    /// <param name="options">Configured thresholds and advisory phrases.</param>
    public static HybridExecutionResult Decide(
        RoutingDecision decision,
        string message,
        HybridExecutionContext context,
        PlanPersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(options);

        bool councilIntent = IsCouncilIntent(decision);

        // 1) Explicit override — authenticated callers only, ignored when
        //    the requested path is not actually available.
        if (!string.IsNullOrWhiteSpace(context.ForcedPath) && !context.AnonymousCaller)
        {
            if (string.Equals(context.ForcedPath, ExecutionPath.Fast, StringComparison.OrdinalIgnoreCase))
            {
                return new HybridExecutionResult(ExecutionPath.Fast, Forced: true, HybridExecutionReason.ForceOverride);
            }

            if (string.Equals(context.ForcedPath, ExecutionPath.Plan, StringComparison.OrdinalIgnoreCase)
                && context.PlannerAvailable)
            {
                return new HybridExecutionResult(ExecutionPath.Plan, Forced: true, HybridExecutionReason.ForceOverride);
            }

            // Force=plan but plan orchestrator not wired — fall through to the
            // deterministic pipeline. The endpoint logs the ignored override so
            // an operator sees why "force plan" did nothing.
        }

        // 2) Council dedicated route.
        if (councilIntent)
        {
            return new HybridExecutionResult(ExecutionPath.Council, Forced: false, HybridExecutionReason.CouncilIntent);
        }

        // 3) Planner unavailable — fast path is the only reachable destination.
        if (context.AnonymousCaller)
        {
            return new HybridExecutionResult(ExecutionPath.Fast, Forced: false, HybridExecutionReason.AnonymousCaller);
        }

        if (!context.PlannerAvailable)
        {
            return new HybridExecutionResult(ExecutionPath.Fast, Forced: false, HybridExecutionReason.PlannerUnavailable);
        }

        // 4) Multi-domain (detected intents ≥ configured threshold).
        int planThreshold = options.MinDetectedIntentsForPlan < 1 ? 1 : options.MinDetectedIntentsForPlan;
        IReadOnlyList<string>? detected = decision.DetectedIntents;
        if (detected is { Count: > 0 } && detected.Count >= planThreshold)
        {
            return new HybridExecutionResult(ExecutionPath.Plan, Forced: false, HybridExecutionReason.MultiDomain);
        }

        // 5) Low confidence — the router is not sure enough to trust a
        //    single specialist.
        if (decision.Confidence < options.MinConfidenceForFastPath)
        {
            return new HybridExecutionResult(ExecutionPath.Plan, Forced: false, HybridExecutionReason.LowConfidence);
        }

        // 6) Advisory phrase. Uses OrdinalIgnoreCase Contains so no
        //    per-call allocation is required to match.
        IList<string> phrases = options.AdvisoryPhrases;
        if (phrases is { Count: > 0 })
        {
            for (int i = 0; i < phrases.Count; i++)
            {
                string phrase = phrases[i];
                if (!string.IsNullOrWhiteSpace(phrase)
                    && message.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                {
                    return new HybridExecutionResult(ExecutionPath.Plan, Forced: false, HybridExecutionReason.AdvisoryPhrase);
                }
            }
        }

        // 7) Default — fast path.
        return new HybridExecutionResult(ExecutionPath.Fast, Forced: false, HybridExecutionReason.SingleDomain);
    }

    /// <summary>
    /// True when the routing decision would trigger the consensus-council
    /// interception. Kept identical to the endpoint's own check and the
    /// <c>AnonymousChatRestrictions</c> check so the three trigger on
    /// exactly the same condition.
    /// </summary>
    public static bool IsCouncilIntent(RoutingDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (string.Equals(decision.Intent, AgentIntent.PortfolioHealth, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        IReadOnlyList<string>? intents = decision.DetectedIntents;
        if (intents is null) return false;
        for (int i = 0; i < intents.Count; i++)
        {
            if (string.Equals(intents[i], AgentIntent.PortfolioHealth, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Endpoint-side signals the decider consumes. Kept as a struct so
/// constructing one is allocation-free on the hot path.
/// </summary>
public readonly record struct HybridExecutionContext(
    bool AnonymousCaller,
    bool PlannerAvailable,
    string? ForcedPath);

/// <summary>
/// Outcome of the hybrid decision. <see cref="Path"/> is one of the
/// <see cref="ExecutionPath"/> constants; <see cref="Forced"/> is set only
/// when the resolution honoured an explicit user override.
/// </summary>
/// <param name="Path">Chosen execution path (fast / plan / council).</param>
/// <param name="Forced">True when the path came from an explicit user override.</param>
/// <param name="Reason">Stable reason code for telemetry and diagnostics.</param>
public readonly record struct HybridExecutionResult(
    string Path,
    bool Forced,
    string Reason);

/// <summary>
/// Stable, testable reason codes explaining why the decider chose a path.
/// Emitted on the routing span as <c>agent.routing.decision_reason</c>.
/// </summary>
public static class HybridExecutionReason
{
    public const string ForceOverride = "force_override";
    public const string CouncilIntent = "council_intent";
    public const string AnonymousCaller = "anonymous_caller";
    public const string PlannerUnavailable = "planner_unavailable";
    public const string MultiDomain = "multi_domain";
    public const string LowConfidence = "low_confidence";
    public const string AdvisoryPhrase = "advisory_phrase";
    public const string SingleDomain = "single_domain";
}

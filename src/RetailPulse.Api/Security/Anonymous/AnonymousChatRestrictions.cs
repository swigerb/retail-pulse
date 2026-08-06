using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Security.Anonymous;

/// <summary>
/// Intent-level restrictions applied to an Anonymous-mode <c>POST /api/chat</c> turn, enforced in
/// the endpoint BEFORE specialist selection/execution and BEFORE any alternate in-process
/// orchestrator (the consensus council) is convened.
///
/// These close two chat-internal bypasses that tool filtering alone cannot:
/// <list type="bullet">
///   <item><b>Memory management</b> — the <c>MemoryManagementAgent</c> calls
///     <c>IConversationMemory.StoreAsync</c>/<c>ForgetAsync</c> DIRECTLY (it uses no AI tools), so
///     the anonymous write-tool filter never sees it. If the router classifies a prompt (e.g.
///     "remember that ...") as <see cref="AgentIntent.MemoryManagement"/>, the endpoint returns a
///     standard safe refusal so that agent never runs — no model call, no memory row written.</item>
///   <item><b>Consensus council / portfolio health</b> — the council interception convenes
///     <c>IConsensusCouncil</c>, which fans out multiple model calls and returns EARLY, bypassing
///     the single accounted budget/audit/guardrail path. For an anonymous session the endpoint
///     refuses before the council is convened, so no council model call is ever made.</item>
/// </list>
/// Both are hard refusals (deterministic, no model), independent of and in addition to the
/// tool-filter and cache/memory-disabled narrowing already applied to the retained single
/// <c>AgentExecutionPipeline</c> model path.
/// </summary>
public static class AnonymousChatRestrictions
{
    /// <summary>Standard safe refusal returned when an anonymous turn is classified as memory management.</summary>
    public const string MemoryRefusalMessage =
        "Saving or clearing remembered preferences isn't available in anonymous mode. " +
        "Your session keeps no durable memory between conversations. " +
        "You can still ask about demand, promotions, supply, margins, competitive and field insights.";

    /// <summary>Standard safe refusal returned when an anonymous turn would convene the portfolio-health council.</summary>
    public const string CouncilRefusalMessage =
        "The multi-agent Portfolio Health Council isn't available in anonymous mode. " +
        "Ask a single-topic question — for example demand, promotions, supply, margins, " +
        "competitive or field insights — and a specialist will answer.";

    /// <summary>
    /// True when the routing decision targets memory management (the direct-write
    /// <c>MemoryManagementAgent</c>), by primary intent or any detected multi-intent.
    /// </summary>
    public static bool IsMemoryManagementIntent(RoutingDecision decision) =>
        MatchesIntent(decision, AgentIntent.MemoryManagement);

    /// <summary>
    /// True when the routing decision would trigger the consensus-council interception
    /// (portfolio health), by primary intent or any detected multi-intent. Mirrors the council
    /// interception's own trigger so the refusal fires on exactly the same condition.
    /// </summary>
    public static bool IsCouncilIntent(RoutingDecision decision) =>
        MatchesIntent(decision, AgentIntent.PortfolioHealth);

    private static bool MatchesIntent(RoutingDecision decision, string intent)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return string.Equals(decision.Intent, intent, StringComparison.OrdinalIgnoreCase)
            || decision.DetectedIntents?.Any(i => string.Equals(i, intent, StringComparison.OrdinalIgnoreCase)) == true;
    }
}

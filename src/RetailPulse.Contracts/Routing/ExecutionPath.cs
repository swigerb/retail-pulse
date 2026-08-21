namespace RetailPulse.Contracts.Routing;

/// <summary>
/// Stable UI + telemetry contract values for the hybrid execution decision
/// made in front of the chat pipeline (issue #95). The three values are the
/// only routes the endpoint can take; anything else is a programming error.
///
/// <list type="bullet">
///   <item><see cref="Fast"/> — the single-specialist single-shot path
///     (today's default). One router classification, one specialist call,
///     no plan generation, no plan review.</item>
///   <item><see cref="Plan"/> — the workflow-backed plan-first path
///     (issue #93). The plan orchestrator generates a plan, optionally
///     suspends for review (#94), and composes a reply from specialist
///     steps.</item>
///   <item><see cref="Council"/> — the dedicated consensus council
///     interception (portfolio-health), unchanged from before this issue.
///     Kept as its own contract value so telemetry and the UI can
///     distinguish "took the council branch" from "took the plan branch".</item>
/// </list>
///
/// The value is surfaced on <see cref="RoutingInfo.ExecutionPath"/> and
/// tagged on the routing Activity as <c>agent.routing.path</c>.
/// </summary>
public static class ExecutionPath
{
    /// <summary>Single-specialist single-shot execution (today's default).</summary>
    public const string Fast = "fast";

    /// <summary>Plan-first orchestration path (issue #93).</summary>
    public const string Plan = "plan";

    /// <summary>Consensus-council interception (portfolio-health). Dedicated trigger; unchanged.</summary>
    public const string Council = "council";

    /// <summary>Every valid contract value for validation/allow-listing.</summary>
    public static readonly IReadOnlyList<string> All = [Fast, Plan, Council];

    /// <summary>
    /// True when <paramref name="value"/> is a stable UI/telemetry contract
    /// value. Case-insensitive because the wire format is user-controlled
    /// (query params, request bodies) and callers should not have to match
    /// our exact casing.
    /// </summary>
    public static bool IsKnown(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && (string.Equals(value, Fast, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, Plan, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, Council, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when <paramref name="value"/> is one of the paths the user is
    /// allowed to force. Council is a router-controlled destination and is
    /// never a user override — the council keeps its own dedicated trigger.
    /// </summary>
    public static bool IsForceable(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && (string.Equals(value, Fast, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, Plan, StringComparison.OrdinalIgnoreCase));
    }
}

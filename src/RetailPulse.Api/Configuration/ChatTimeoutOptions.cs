namespace RetailPulse.Api.Configuration;

/// <summary>
/// Separate ceilings for the single-shot chat pipeline and long-running plan
/// execution (issue #92). The pre-Wave-2 pipeline used a single hard-coded 90s
/// wall on every /api/chat request; a multi-step plan run through the hybrid
/// decider (issue #95) can honestly need longer than that, but globally raising
/// 90s would silently unbound single-shot requests. Two knobs, two ceilings.
///
/// <para>Defaults preserve current single-shot behavior at 90s. The plan
/// ceiling defaults to 6 minutes — larger than <c>PlanPersistenceOptions.PlanTimeout</c>
/// (default 3m) plus a small safety buffer, so the plan orchestrator's own timeout
/// fires first with a plan-specific failure reason rather than the request seam
/// timing out with the generic 504 message. Operators tuning either value should
/// keep this invariant: <c>Plan &gt;= PlanPersistence.PlanTimeout + 60s</c>.</para>
/// </summary>
public sealed class ChatTimeoutOptions
{
    public const string SectionName = "ChatTimeout";

    /// <summary>
    /// Ceiling applied to every /api/chat request at entry, and to any
    /// request that resolves to the single-specialist fast path. Default 90s.
    /// </summary>
    public TimeSpan SingleShot { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Ceiling applied only to requests admitted to the plan-first path by
    /// the hybrid execution decider (issue #95). Default 6 minutes.
    /// </summary>
    public TimeSpan Plan { get; set; } = TimeSpan.FromMinutes(6);
}

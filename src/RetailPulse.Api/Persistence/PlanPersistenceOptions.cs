namespace RetailPulse.Api.Persistence;

/// <summary>
/// Configuration for the durable plan store (issue #93). Sibling of
/// <see cref="SessionPersistenceOptions"/>: same operator ergonomics, same
/// default-off posture. The plan store is intentionally gated separately so an
/// operator can turn plan orchestration on/off without touching session
/// history and vice versa — the two features often ship together but their
/// storage lifecycles differ (plans age out much faster).
/// </summary>
public sealed class PlanPersistenceOptions
{
    public const string SectionName = "PlanPersistence";

    /// <summary>
    /// Master switch. When false, the plan store singleton is not registered,
    /// the cleanup hosted service does not run, no database file is created or
    /// opened, and the chat pipeline routes every request through the existing
    /// single-specialist path — so the API behaves exactly as it did before
    /// this feature was added.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Retention TTL — plans whose last update is older than this are purged by
    /// the background cleanup service. 14 days is deliberately shorter than the
    /// 30-day session default: a plan is an operational record of one turn's
    /// orchestration, not a durable conversation history.
    /// </summary>
    public TimeSpan RetentionTtl { get; set; } = TimeSpan.FromDays(14);

    /// <summary>Cleanup sweep cadence.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Hard cap on plan step count. Enforced at planner-output validation time
    /// so a large-context model cannot silently produce a 12-step plan that
    /// exhausts the shared tool budget. Design review pinned this at 5.
    /// </summary>
    public int MaxStepCount { get; set; } = 5;

    /// <summary>Per-step execution timeout, bounded and configurable.</summary>
    public TimeSpan StepTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Overall plan execution timeout. Prevents an orchestration hang from stalling a request forever.</summary>
    public TimeSpan PlanTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Detected-intent threshold for admitting a request into the plan-first
    /// path. Anything below this stays on the single-specialist path — the
    /// existing router already handles single-domain requests perfectly well
    /// and the plan orchestrator is intentionally reserved for multi-domain
    /// work.
    /// </summary>
    public int MinDetectedIntentsForPlan { get; set; } = 2;
}

using RetailPulse.Contracts.Persistence;

namespace RetailPulse.Api.Persistence;

/// <summary>
/// Durable plan/step store. Sibling of <see cref="ISessionStore"/>: subject-scoped
/// reads and writes, no cross-subject leakage, and SMB-safe pragmas via
/// <see cref="Data.SqliteMount"/>. Introduced by issue #93 for plan-first
/// orchestration; anonymous callers never persist plans (mirroring session
/// persistence), so every read filters by the caller's subject at the SQL layer.
/// A plan id owned by a different subject resolves to <c>null</c>, which the
/// endpoint layer surfaces as a 404 — the same probe-resistant contract sessions
/// already enforce.
/// </summary>
public interface IPlanStore
{
    /// <summary>
    /// Create a plan row for this subject and append its ordered step list in a
    /// single transaction. Called once by the orchestrator after the planner
    /// produces the initial step list; step results are then updated in place
    /// via <see cref="UpdateStepAsync"/> and the plan is finalized via
    /// <see cref="FinalizePlanAsync"/>.
    /// </summary>
    Task CreatePlanAsync(PlanWrite plan, CancellationToken ct = default);

    /// <summary>
    /// Update the plan-level status/failure/reason/token totals. Called by the
    /// orchestrator at status transitions (running/completed/failed/…) so the
    /// persisted view always reflects the live state; a restart therefore
    /// rehydrates whatever the last committed status was.
    /// </summary>
    Task UpdatePlanStatusAsync(PlanStatusUpdate update, CancellationToken ct = default);

    /// <summary>
    /// Atomic conditional status transition: writes <paramref name="toStatus"/>
    /// onto the plan row for <paramref name="subject"/> only when its current
    /// status equals <paramref name="fromStatus"/>. Returns <c>true</c> when
    /// exactly one row transitioned (the caller "won" the transition), and
    /// <c>false</c> when another caller already advanced the row.
    ///
    /// <para>
    /// The plan-review resume path uses this to claim the effective execution
    /// for exactly one caller when two concurrent decision / clarification
    /// submissions race the same approval row: <see cref="IApprovalGate.RespondAsync"/>
    /// records exactly one persisted winner, then each caller fires
    /// <c>ResolveAsync</c>; without this claim both would call
    /// <c>UpdatePlanStatusAsync(Running)</c>, run the executor twice, and
    /// broadcast a duplicate <c>plan_final_response</c>. The conditional
    /// UPDATE keyed on the pre-transition status collapses that race to a
    /// single execution and a single broadcast.
    /// </para>
    /// </summary>
    Task<bool> TryTransitionStatusAsync(
        string planId,
        string subject,
        string fromStatus,
        string toStatus,
        CancellationToken ct = default);

    /// <summary>
    /// Update one step in place — status transition, result content, tokens,
    /// duration, timestamps, or error. Every field except the primary key is
    /// nullable so the orchestrator can commit whatever it knows at each
    /// transition without a read-modify-write.
    /// </summary>
    Task UpdateStepAsync(PlanStepUpdate update, CancellationToken ct = default);

    /// <summary>
    /// Atomically synchronize the persisted step rows for a plan from a given
    /// starting <see cref="StepIndex"/> onward. Deletes any existing
    /// <c>PlanSteps</c> rows with <c>StepIndex &gt;= fromStepIndex</c> and
    /// inserts the supplied <paramref name="steps"/> — both operations in a
    /// single transaction so a concurrent reader never sees a torn view. Rows
    /// with <c>StepIndex &lt; fromStepIndex</c> are preserved verbatim so the
    /// cumulative transcript across a clarification-resume (paused prefix +
    /// downstream tail) stays intact.
    /// <para>
    /// Introduced (#144 follow-up) so the plan-review resume path can persist
    /// its round-specific step rows BEFORE <see cref="UpdateStepAsync"/> runs;
    /// the executor's per-step UPDATE only touches existing rows, so a resume
    /// that emitted new <c>{planId}-r{round}-s{n}</c> ids without inserting
    /// them first would silently no-op every step transition and hydrate as a
    /// zero-step plan.
    /// </para>
    /// </summary>
    Task ReplacePlanStepsFromIndexAsync(
        string planId,
        string subject,
        int fromStepIndex,
        IReadOnlyList<PlanStepWrite> steps,
        CancellationToken ct = default);

    /// <summary>List this subject's plans, newest activity first. Enforces the caller filter at SQL.</summary>
    Task<IReadOnlyList<PlanSummaryDto>> ListPlansForSubjectAsync(
        string subject, CancellationToken ct = default);

    /// <summary>Rehydrate one plan with its ordered steps, or <c>null</c> when unknown or cross-subject.</summary>
    Task<PlanDetailDto?> GetPlanAsync(
        string subject, string planId, CancellationToken ct = default);

    /// <summary>Delete a plan (and every step under it) when owned by the caller.</summary>
    Task<bool> DeletePlanAsync(
        string subject, string planId, CancellationToken ct = default);

    /// <summary>Retention sweep — evicts plans/steps whose plan updated_at is older than the cutoff.</summary>
    Task<PlanCleanupResult> PurgeExpiredAsync(
        DateTimeOffset olderThan, CancellationToken ct = default);
}

/// <summary>
/// Retention sweep result for <see cref="IPlanStore.PurgeExpiredAsync"/>.
/// </summary>
public readonly record struct PlanCleanupResult(int PlansDeleted, int StepsDeleted);

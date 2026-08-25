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
    ///
    /// <para>
    /// Terminal-status contract (issue #149): when <see cref="PlanStatusUpdate.Status"/>
    /// is one of <see cref="PlanStatus.Completed"/>,
    /// <see cref="PlanStatus.Failed"/>,
    /// <see cref="PlanStatus.Cancelled"/>, or
    /// <see cref="PlanStatus.Unusable"/>, the implementation
    /// MUST atomically transition every remaining <see cref="PlanStepStatus.Pending"/>
    /// or <see cref="PlanStepStatus.Running"/> step row
    /// for the plan to <see cref="PlanStepStatus.Skipped"/>
    /// in the same write. This prevents orphaned step rows lingering after
    /// review-approved execution supersedes the initial <c>{planId}-s{i}</c>
    /// rows with round-scoped <c>{planId}-r{round}-s{i}</c> rows (see
    /// <c>PlanOrchestrator.SuspendForReviewAsync</c> and
    /// <c>PlanReviewCompletionService.ExecuteApprovedPlanAsync</c>).
    /// </para>
    /// </summary>
    Task UpdatePlanStatusAsync(PlanStatusUpdate update, CancellationToken ct = default);

    /// <summary>
    /// Update one step in place — status transition, result content, tokens,
    /// duration, timestamps, or error. Every field except the primary key is
    /// nullable so the orchestrator can commit whatever it knows at each
    /// transition without a read-modify-write.
    /// </summary>
    Task UpdateStepAsync(PlanStepUpdate update, CancellationToken ct = default);

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

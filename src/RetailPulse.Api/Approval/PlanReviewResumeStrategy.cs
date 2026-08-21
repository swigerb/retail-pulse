using RetailPulse.Contracts.Approval;

namespace RetailPulse.Api.Approval;

/// <summary>
/// Wave 2 resume strategy for plan review (#94). Extends the #91 seam without
/// introducing a second reconciliation mechanism: reconciliation still walks
/// every Pending row exactly once, and the strategy branches on
/// <see cref="ApprovalContext.Kind"/> to decide the per-row action.
///
/// <para>
/// Tool rows (<see cref="ApprovalKind.Tool"/> — the ApprovalTool default) keep
/// their #91 behavior: no execution checkpoint exists for a single blocked-tool
/// call, so the row is terminated with <see cref="ApprovalResumeAction.OrphanTerminal"/>.
/// This is byte-for-byte identical to <see cref="OrphanUnresumableStrategy"/> for
/// tool rows, so registering this strategy is a strict superset — never a change
/// in existing behavior for the ApprovalTool.
/// </para>
///
/// <para>
/// Plan review and clarification rows (<see cref="ApprovalKind.PlanReview"/> /
/// <see cref="ApprovalKind.Clarification"/>) return
/// <see cref="ApprovalResumeAction.Resume"/>. The gate re-owns the row to the
/// current process and refreshes its heartbeat, so the next reconciliation pass
/// leaves it alone. When the human decision later arrives via the plan-review
/// endpoint, that endpoint executes the persisted plan directly through the
/// same coordinator seam — the row's <see cref="ApprovalRequest.ResponsePayload"/>
/// (edited plan or feedback) is the authoritative source of truth. The framework
/// checkpoint written by <see cref="PlanReviewCoordinator"/> is available as an
/// operational replay aid, but the coordinator does not require it to make
/// progress after restart.
/// </para>
/// </summary>
public sealed class PlanReviewResumeStrategy : IApprovalResumeStrategy
{
    public Task<ApprovalResumeAction> DecideAsync(ApprovalRequest orphaned, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orphaned);

        string kind = orphaned.Context?.Kind ?? ApprovalKind.Tool;
        ApprovalResumeAction action = kind switch
        {
            ApprovalKind.PlanReview => ApprovalResumeAction.Resume,
            ApprovalKind.Clarification => ApprovalResumeAction.Resume,
            _ => ApprovalResumeAction.OrphanTerminal,
        };
        return Task.FromResult(action);
    }
}

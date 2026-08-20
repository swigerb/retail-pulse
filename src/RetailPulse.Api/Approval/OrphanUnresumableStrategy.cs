using RetailPulse.Contracts.Approval;

namespace RetailPulse.Api.Approval;

/// <summary>
/// Wave 1 default <see cref="IApprovalResumeStrategy"/>: every Pending approval
/// whose owning execution did not survive the restart is deterministically closed
/// with <see cref="ApprovalDecision.Orphaned"/> (terminal reason
/// <c>OrphanedOnRestart</c>).
///
/// <para>
/// Rationale: Wave 1 has no execution checkpoint layer, so nothing exists to
/// resume — leaving the row Pending would silently reproduce the original bug
/// (approving later has no effect because no in-process waiter is listening).
/// Closing terminally means the history surface, the endpoint, and any Wave 2
/// consumer see exactly one outcome per request.
/// </para>
///
/// <para>
/// Wave 2 (issues #93/#94) replaces this registration with a strategy that
/// consults the durable session store (issue #90) through the
/// <see cref="ApprovalContext.SessionId"/> / <see cref="ApprovalContext.ConversationId"/>
/// correlation columns already persisted on every row. When a checkpoint can be
/// resumed the strategy returns <see cref="ApprovalResumeAction.Resume"/> and the
/// gate refreshes the owning instance so the resumed execution owns the terminal
/// transition. No schema migration is required for that swap — the seam is
/// entirely in the DI registration.
/// </para>
/// </summary>
public sealed class OrphanUnresumableStrategy : IApprovalResumeStrategy
{
    public Task<ApprovalResumeAction> DecideAsync(ApprovalRequest orphaned, CancellationToken ct = default)
        => Task.FromResult(ApprovalResumeAction.OrphanTerminal);
}

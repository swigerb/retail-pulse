namespace RetailPulse.Contracts.Approval;

/// <summary>
/// Wave 2 resume seam for approval reconciliation (see issues #93/#94). At startup
/// the approval gate walks every <see cref="ApprovalDecision.Pending"/> row that
/// was written by a previous process (identified via the persisted agent-instance
/// id) and asks this strategy what to do with it.
///
/// <para>
/// Wave 1 ships <c>OrphanUnresumableStrategy</c>, which always returns
/// <see cref="ApprovalResumeAction.OrphanTerminal"/> — no execution checkpoint
/// layer exists yet, so a request whose in-process waiter died cannot be resumed
/// and must be closed deterministically. This preserves the user-visible contract
/// (exactly one terminal outcome per request) instead of leaving a silent orphan.
/// </para>
///
/// <para>
/// Wave 2 replaces the registration with a strategy that consults the durable
/// session/conversation store (issue #90) via
/// <see cref="ApprovalContext.SessionId"/> / <see cref="ApprovalContext.ConversationId"/>
/// to rehydrate a checkpointed execution and return
/// <see cref="ApprovalResumeAction.Resume"/>. The gate keeps the row Pending in that
/// case and the resumed execution owns the terminal transition. No schema or gate
/// changes are required for the swap.
/// </para>
/// </summary>
public interface IApprovalResumeStrategy
{
    /// <summary>
    /// Decide the reconciliation action for a Pending approval whose owning agent
    /// instance is no longer this process. Implementations must be idempotent and
    /// side-effect free — the gate performs the SQL transition itself, so a strategy
    /// that runs twice for the same request produces the same answer.
    /// </summary>
    Task<ApprovalResumeAction> DecideAsync(ApprovalRequest orphaned, CancellationToken ct = default);
}

/// <summary>
/// Outcome the resume strategy asks the gate to apply.
/// </summary>
public enum ApprovalResumeAction
{
    /// <summary>
    /// Transition the row to <see cref="ApprovalDecision.Orphaned"/> with terminal
    /// reason <c>OrphanedOnRestart</c>. The history surface shows a clear terminal
    /// state instead of a silent stuck Pending.
    /// </summary>
    OrphanTerminal,

    /// <summary>
    /// Leave the row Pending — Wave 2 will rehydrate the checkpointed execution and
    /// the resumed waiter owns the terminal transition. The gate refreshes the row's
    /// owning instance and heartbeat so the next reconciliation pass leaves it alone.
    /// </summary>
    Resume
}

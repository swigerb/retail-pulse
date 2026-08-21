namespace RetailPulse.Contracts.Approval;

/// <summary>
/// Human-in-the-loop approval gate. Agents call this to pause execution
/// and wait for a human decision before proceeding with high-impact actions.
/// </summary>
public interface IApprovalGate
{
    /// <summary>
    /// Creates a new approval request and persists it. Returns immediately
    /// with the request metadata (does not block for a decision).
    /// </summary>
    Task<ApprovalRequest> RequestApprovalAsync(ApprovalContext context, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the current state of an approval request without blocking.
    /// </summary>
    Task<ApprovalResult> GetResultAsync(string requestId, CancellationToken ct = default);

    /// <summary>
    /// Polls until a decision is recorded or the timeout elapses.
    /// If the timeout expires, the waiter attempts to transition the request to
    /// <see cref="ApprovalDecision.TimedOut"/> via a single conditional SQL update
    /// from <see cref="ApprovalDecision.Pending"/>. On losing that race (a human
    /// resolved concurrently), the returned <see cref="ApprovalResult"/> reflects
    /// the actual persisted winner so the caller and the row agree on exactly one
    /// terminal outcome — never a double resolution.
    /// </summary>
    Task<ApprovalResult> WaitForApprovalAsync(string requestId, TimeSpan? timeout = null, CancellationToken ct = default);

    /// <summary>
    /// Records a human decision for a pending approval request via a conditional
    /// <c>Pending → terminal</c> update, and returns the actual persisted winner
    /// so callers cannot echo a decision they did not win.
    ///
    /// <para>
    /// If the requested decision wins the race, the returned
    /// <see cref="ApprovalResult"/> reflects that decision, its terminal reason
    /// (<c>HumanApproved</c>/<c>HumanRejected</c>/<c>HumanModified</c>), the comment
    /// supplied here, and the timestamp of the write. If the row was already
    /// terminal (timeout, orphan reconciliation, or an earlier human response),
    /// the returned result is the previously persisted winner — never a synthetic
    /// echo of <paramref name="decision"/>. Endpoint and SignalR payloads must
    /// report the returned <see cref="ApprovalResult"/>, not the caller-requested
    /// decision, so exactly one user-visible outcome is observable end-to-end.
    /// </para>
    ///
    /// <para>
    /// Plan review (#94) reuses this same storage and code path so plan and
    /// tool approvals share one audit trail. Callers pass an optional
    /// <paramref name="responsePayload"/> to persist edited plan JSON,
    /// rejection feedback, or a clarification answer alongside the decision;
    /// the payload is opaque to the gate and read back through
    /// <see cref="ApprovalResult.ResponsePayload"/>.
    /// </para>
    /// </summary>
    Task<ApprovalResult> RespondAsync(
        string requestId,
        ApprovalDecision decision,
        string? comment = null,
        string? responsePayload = null,
        CancellationToken ct = default);

    /// <summary>
    /// Lists all pending approval requests for a given user.
    /// </summary>
    Task<IReadOnlyList<ApprovalRequest>> GetPendingAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the audit trail of past approval decisions, ordered most-recent first.
    /// </summary>
    Task<IReadOnlyList<ApprovalRequest>> GetHistoryAsync(int limit = 50, CancellationToken ct = default);
}

/// <summary>
/// Describes the action requiring human approval.
/// </summary>
public record ApprovalContext(
    string AgentId,
    string UserId,
    string Action,
    string Impact,
    string Urgency,
    string Reasoning,
    // Optional correlation to the durable session/conversation store (issue #90). Wave 1
    // producers may leave both null; Wave 2 populates them so the resume strategy can
    // rehydrate a checkpointed execution instead of orphaning on restart.
    string? SessionId = null,
    string? ConversationId = null,
    // Category tag persisted verbatim so plan review (#94) rows can be distinguished
    // from single-tool ApprovalTool rows without changing the base contract. Defaults
    // to <see cref="ApprovalKind.Tool"/> so every existing producer keeps its exact
    // stored shape and existing history/pending queries continue to observe the
    // same rows they always did.
    string Kind = ApprovalKind.Tool,
    // Optional correlation back to a plan (#93/#94). Populated for plan review and
    // clarification rows so the endpoint layer can list decisions per plan and the
    // resume strategy can look up the workflow checkpoint for the same plan id.
    string? PlanId = null,
    // Zero-based replan round for plan review rows. Every reject-with-feedback
    // increments the round; the coordinator enforces a bounded cap so replan can
    // never loop forever.
    int RoundNumber = 0,
    // Opaque JSON payload the coordinator/endpoint stores alongside the request
    // (e.g. the plan proposal or clarification question). Never inspected by the
    // gate itself.
    string? Payload = null
);

/// <summary>
/// A persisted approval request with expiry tracking.
/// </summary>
public record ApprovalRequest(
    string RequestId,
    ApprovalContext Context,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    ApprovalDecision Decision = ApprovalDecision.Pending,
    string? Comment = null,
    DateTimeOffset? RespondedAt = null,
    // Human-readable reason for the terminal state (e.g., "HumanApproved",
    // "HumanRejected", "HumanModified", "Timeout", "OrphanedOnRestart"). Null while
    // the request is still <see cref="ApprovalDecision.Pending"/>. Additive with a
    // default so existing constructors continue to compile.
    string? TerminalReason = null,
    // Opaque JSON payload written by the human responder (e.g. the edited plan or
    // the clarification answer). Additive; existing callers ignore it. See
    // <see cref="ApprovalResult.ResponsePayload"/>.
    string? ResponsePayload = null
);

/// <summary>
/// The outcome of an approval request.
/// </summary>
public record ApprovalResult(
    string RequestId,
    ApprovalDecision Decision,
    string? Comment,
    DateTimeOffset? RespondedAt,
    // Distinguishable terminal reason (see <see cref="ApprovalRequest.TerminalReason"/>).
    // Additive with a default so the existing surface remains binary-compatible for
    // callers that only care about <see cref="Decision"/>.
    string? TerminalReason = null,
    // Opaque JSON payload written by the human responder. Additive default so
    // existing tool-approval callers stay identical. Plan review (#94) reads this
    // to obtain edited plans, rejection feedback, and clarification answers.
    string? ResponsePayload = null
);

public enum ApprovalDecision
{
    Pending,
    Approved,
    Rejected,
    Modified,
    TimedOut,
    // Startup reconciliation terminated a request whose owning execution did not
    // survive a restart and could not be resumed from a checkpoint. See
    // <see cref="IApprovalResumeStrategy"/> for how Wave 2 replaces this default.
    Orphaned
}

/// <summary>
/// Category tag stored on every approval row so plan review (#94), plan
/// clarification (#94), and the pre-existing single-tool ApprovalTool path can
/// share one durable table without cross-contaminating their surfaces. The gate
/// itself does not interpret these values; the coordinator and endpoint layers
/// route on them.
/// </summary>
public static class ApprovalKind
{
    /// <summary>Default — a single-tool ApprovalTool request (#91 behavior).</summary>
    public const string Tool = "tool";

    /// <summary>Plan-level review before execution (#94).</summary>
    public const string PlanReview = "plan_review";

    /// <summary>Mid-plan clarification round-trip (#94).</summary>
    public const string Clarification = "clarification";
}

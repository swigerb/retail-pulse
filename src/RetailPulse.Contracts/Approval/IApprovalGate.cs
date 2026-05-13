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
    /// If the timeout expires, the decision is automatically set to <see cref="ApprovalDecision.TimedOut"/>.
    /// </summary>
    Task<ApprovalResult> WaitForApprovalAsync(string requestId, TimeSpan? timeout = null, CancellationToken ct = default);

    /// <summary>
    /// Records a human decision for a pending approval request.
    /// </summary>
    Task RespondAsync(string requestId, ApprovalDecision decision, string? comment = null, CancellationToken ct = default);

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
    string Reasoning
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
    DateTimeOffset? RespondedAt = null
);

/// <summary>
/// The outcome of an approval request.
/// </summary>
public record ApprovalResult(
    string RequestId,
    ApprovalDecision Decision,
    string? Comment,
    DateTimeOffset? RespondedAt
);

public enum ApprovalDecision
{
    Pending,
    Approved,
    Rejected,
    Modified,
    TimedOut
}

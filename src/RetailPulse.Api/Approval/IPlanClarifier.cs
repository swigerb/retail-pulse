using RetailPulse.Contracts.Approval;

namespace RetailPulse.Api.Approval;

/// <summary>
/// Mid-plan clarification round-trip (#94). A step whose specialist needs
/// reviewer input to proceed pauses via <see cref="AskAsync"/>: the coordinator
/// creates an approval row with <see cref="ApprovalKind.Clarification"/>,
/// persists the question as the row's payload, blocks on
/// <see cref="IApprovalGate.WaitForApprovalAsync"/>, and returns the answer
/// when it arrives (or times out with a deterministic terminal reason).
///
/// <para>
/// Sharing the same <see cref="IApprovalGate"/> storage means clarification
/// prompts appear in the shared history / audit trail alongside plan reviews and
/// single-tool approvals — one durable surface, one restart posture. Cross-
/// subject rejection and injected-clock timeout are inherited from the gate;
/// this service does not add a second policy path.
/// </para>
/// </summary>
public interface IPlanClarifier
{
    /// <summary>
    /// Ask the reviewer a clarification question. Blocks until the answer arrives
    /// or the configured clarification timeout elapses. The returned
    /// <see cref="PlanClarificationResult"/> is authoritative — its
    /// <see cref="PlanClarificationResult.IsAnswered"/> is false only for
    /// timeouts, orphaned rows, or unusable answers, and the caller records that
    /// terminal state without hanging.
    ///
    /// <para>Retained for coordinator-level tests. Production callers use
    /// <see cref="PlanClarifier.OpenAsync"/> (non-blocking) + endpoint /
    /// completion-service resume to avoid holding a request thread on the
    /// clarification timeout.</para>
    /// </summary>
    Task<PlanClarificationResult> AskAsync(
        PlanClarificationPrompt prompt,
        string subject,
        CancellationToken ct = default);
}

/// <summary>
/// Result of a clarification round-trip.
/// </summary>
public sealed record PlanClarificationResult(
    bool IsAnswered,
    string? Answer,
    string TerminalReason,
    string RequestId);

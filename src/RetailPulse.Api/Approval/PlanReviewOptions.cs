namespace RetailPulse.Api.Approval;

/// <summary>
/// Configuration for the plan review gate (#94). Behavior-safe by default: the
/// feature is OFF unless <see cref="Enabled"/> is explicitly true, so #93's hot
/// path (plan generation → execution without a human pause) keeps its exact
/// pre-#94 shape. Every timeout is bounded and every replan cap is finite —
/// the coordinator is architecturally incapable of hanging on human input.
/// </summary>
public sealed class PlanReviewOptions
{
    public const string SectionName = "PlanReview";

    /// <summary>
    /// Master switch. False (default) skips the plan review gate entirely — the
    /// planner produces a plan and the executor runs it immediately, matching
    /// #93's default posture. Setting this to true wires the coordinator into
    /// <see cref="Agents.Planning.PlanOrchestrator"/> and swaps the reconciliation
    /// resume strategy for one that adopts plan-review rows across restarts.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Authoritative timeout for a plan review round. Persisted on the approval
    /// row so a waiter created before a restart still honours the same timeout
    /// after adoption by the new instance. When exceeded, the row transitions to
    /// <see cref="Contracts.Approval.ApprovalDecision.TimedOut"/> and the plan
    /// terminates with terminal reason
    /// <see cref="Contracts.Approval.PlanReviewTerminalReason.ReviewTimedOut"/> —
    /// never an indefinite hang. Default: 30 minutes.
    /// </summary>
    public TimeSpan DefaultReviewTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Maximum reject-with-feedback rounds. Round 0 is the initial plan; a
    /// rejection at round R produces round R+1 if R+1 &lt;= MaxReplanRounds.
    /// Beyond that the coordinator terminates with
    /// <see cref="Contracts.Approval.PlanReviewTerminalReason.ReplanExhausted"/>.
    /// Default: 2 (initial + up to two revised plans).
    /// </summary>
    public int MaxReplanRounds { get; set; } = 2;

    /// <summary>
    /// Timeout for a mid-plan clarification round-trip. Distinct from the review
    /// timeout because a clarification interrupts mid-execution and typically
    /// wants a shorter deadline. Default: 15 minutes.
    /// </summary>
    public TimeSpan ClarificationTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Sub-directory under the data directory where the framework
    /// <c>Microsoft.Agents.AI.Workflows.CheckpointManager</c> stores its JSON
    /// checkpoints for the plan review workflow. One JSON file per session id
    /// (plan id + round). Kept relative so the resolved data directory (which
    /// itself may be ephemeral on this deployment — see DataDirectoryResolver)
    /// remains the single source of truth.
    /// </summary>
    public string CheckpointSubdirectory { get; set; } = "plan-reviews";
}

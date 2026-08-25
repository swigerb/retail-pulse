using RetailPulse.Contracts;

namespace RetailPulse.Contracts.Approval;

/// <summary>
/// Contract types for plan review (#94). Live in the shared contracts project so
/// endpoints, background services, and tests can round-trip payloads without
/// depending on the API's internal orchestration types. Every DTO is JSON-shape
/// stable — the coordinator serializes proposals into
/// <see cref="ApprovalContext.Payload"/> and reviewer responses into
/// <see cref="ApprovalRequest.ResponsePayload"/> using these exact records.
/// </summary>
public static class PlanReviewKinds
{
    /// <summary>Response payload discriminator for an approve decision.</summary>
    public const string Approve = "approve";

    /// <summary>Response payload discriminator for a reject-with-feedback decision.</summary>
    public const string Reject = "reject";

    /// <summary>Response payload discriminator for an edit-then-approve decision.</summary>
    public const string Edit = "edit";
}

/// <summary>
/// One step in a plan proposal. Deliberately mirrors
/// <c>PlannerStep</c> so the payload the reviewer sees is the same shape the
/// planner produced, and edits arrive in the same shape the executor consumes.
/// </summary>
public sealed record PlanReviewStepDto
{
    public required string SpecialistKey { get; init; }
    public required string Intent { get; init; }
    public required string Action { get; init; }
}

/// <summary>
/// Proposal presented to the human reviewer, persisted as JSON on the
/// <see cref="ApprovalRequest"/> row.
/// </summary>
public sealed record PlanReviewProposal
{
    public required string PlanId { get; init; }
    public required int RoundNumber { get; init; }
    public required string Request { get; init; }
    public required IReadOnlyList<PlanReviewStepDto> Steps { get; init; }

    /// <summary>
    /// Populated on rounds > 0 to explain to the reviewer why the plan was revised
    /// (echoed back from the earlier rejection feedback). Null on the initial round.
    /// </summary>
    public string? RevisionReason { get; init; }
}

/// <summary>
/// Human response payload. Discriminated by <see cref="Kind"/>:
/// <list type="bullet">
///   <item><c>approve</c>: <see cref="EditedSteps"/> is null; original plan runs.</item>
///   <item><c>reject</c>: <see cref="Feedback"/> carries the free-text critique the
///     planner will consume for the next round. When the replan cap is exhausted,
///     the coordinator terminates the plan with reason
///     <c>ReplanExhausted</c> — never an indefinite loop.</item>
///   <item><c>edit</c>: <see cref="EditedSteps"/> replaces the original plan in
///     the executor. Every step must reference a specialist in the same live
///     roster the planner saw. An empty <see cref="EditedSteps"/> is treated as
///     "drop the plan" and terminates it with reason <c>EditedToEmpty</c>.</item>
/// </list>
/// </summary>
public sealed record PlanReviewResponsePayload
{
    public required string Kind { get; init; }
    public IReadOnlyList<PlanReviewStepDto>? EditedSteps { get; init; }
    public string? Feedback { get; init; }
}

/// <summary>
/// Mid-plan clarification question the specialist raises through
/// <see cref="Api.Approval.IPlanClarifier"/>. Persisted as JSON on the
/// approval row's <see cref="ApprovalContext.Payload"/>.
/// </summary>
public sealed record PlanClarificationPrompt
{
    public required string PlanId { get; init; }
    public required int StepIndex { get; init; }
    public required string SpecialistKey { get; init; }
    public required string Question { get; init; }
}

/// <summary>
/// Reviewer's response to a clarification prompt. Written into the row's
/// <see cref="ApprovalRequest.ResponsePayload"/>.
/// </summary>
public sealed record PlanClarificationAnswer
{
    public required string Answer { get; init; }
}

/// <summary>
/// Durable snapshot the plan review coordinator writes into the
/// Microsoft.Agents.AI.Workflows checkpoint store at every suspension point.
/// Captures everything a fresh process needs to resume execution when the
/// human decision arrives — the proposed step list for this round, the
/// caller identity, the request text, the roster keys the planner saw, and
/// the current round number. Serialized as JSON so
/// <c>ICheckpointStore&lt;JsonElement&gt;.CreateCheckpointAsync</c> can persist
/// it verbatim.
/// </summary>
public sealed record PlanReviewCheckpointState
{
    /// <summary>Discriminator: <c>review</c> or <c>clarification</c>.</summary>
    public required string Kind { get; init; }
    public required string PlanId { get; init; }
    public required string Subject { get; init; }
    public string? SessionId { get; init; }
    public string? TenantId { get; init; }
    public required string Request { get; init; }
    public required int RoundNumber { get; init; }
    public required IReadOnlyList<PlanReviewStepDto> Steps { get; init; }
    public required IReadOnlyList<string> SpecialistKeys { get; init; }
    public IReadOnlyList<string> DetectedIntents { get; init; } = [];
    public string? TraceId { get; init; }
    public string? ParentSpanId { get; init; }
    public string? PrincipalKey { get; init; }
    public required string ApprovalRequestId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? RevisionReason { get; init; }

    // Clarification-only fields, null on plain plan-review checkpoints.
    public int? PausedAtStepIndex { get; init; }

    /// <summary>
    /// Persisted step id (as materialised by the caller that wrote the initial
    /// plan) of the paused step. Present on clarification checkpoints so the
    /// resume path can transition that specific row out of
    /// <c>Pending</c> and record the reviewer's answer on it. Without this
    /// pointer the answered clarification's row keeps advertising itself as
    /// pending indefinitely (finding 1b, #145).
    /// </summary>
    public string? PausedStepId { get; init; }

    /// <summary>
    /// Steps that already completed before the suspension. Populated on both
    /// clarification checkpoints AND mid-plan review checkpoints so the
    /// completed prefix survives across the reviewer round-trip: without it,
    /// approving a mid-plan <c>[[REPLAN]]</c> silently drops the pre-marker
    /// results and charts on the final reply (finding 2, #145).
    /// </summary>
    public IReadOnlyList<PlanReviewCompletedStep>? CompletedSteps { get; init; }
}

/// <summary>
/// Snapshot of a plan step that already ran (Completed / Failed / Skipped)
/// before the plan suspended for clarification. Persisted inside
/// <see cref="PlanReviewCheckpointState.CompletedSteps"/> so the resume path
/// can rebuild the accumulated context without re-running earlier specialists.
/// </summary>
public sealed record PlanReviewCompletedStep
{
    public required int StepIndex { get; init; }
    public required string SpecialistKey { get; init; }
    public required string Intent { get; init; }
    public required string Action { get; init; }
    public required string Result { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int TotalTokens { get; init; }
    public long DurationMs { get; init; }

    /// <summary>
    /// Charts emitted by the specialist for this pre-suspend step. Persisted
    /// across the clarification checkpoint so the resume path can flatten
    /// them into the final broadcast alongside charts produced by steps
    /// that execute after the reviewer answers. Without this the plan-first
    /// clarification path silently drops every chart a specialist produced
    /// before the [[CLARIFY]] pause, breaking ADR-006's "9 chart types on
    /// both paths" invariant.
    /// </summary>
    public IReadOnlyList<ChartSpec>? Charts { get; init; }
}

/// <summary>
/// Kinds recorded on <see cref="PlanReviewCheckpointState.Kind"/> so the
/// resume path can dispatch to the right handler without re-reading the
/// approval row.
/// </summary>
public static class PlanCheckpointKind
{
    public const string Review = "review";
    public const string Clarification = "clarification";
}

/// <summary>
/// Terminal reason strings written into
/// <see cref="ApprovalResult.TerminalReason"/> and mirrored into the plan store's
/// <c>FailureReason</c> column so the endpoint layer can render a specific
/// user-visible outcome. New reasons are additive; the existing single-tool
/// approval terminal reasons remain unchanged.
/// </summary>
public static class PlanReviewTerminalReason
{
    /// <summary>Reviewer approved the current proposal.</summary>
    public const string ReviewerApproved = "PlanReviewApproved";

    /// <summary>Reviewer approved after amending the step list.</summary>
    public const string ReviewerEdited = "PlanReviewEdited";

    /// <summary>Reviewer rejected with feedback and replan is proceeding.</summary>
    public const string ReviewerRejected = "PlanReviewRejected";

    /// <summary>Replan round cap exhausted; plan terminated without executing.</summary>
    public const string ReplanExhausted = "PlanReviewReplanExhausted";

    /// <summary>Reviewer edited the plan down to zero steps.</summary>
    public const string EditedToEmpty = "PlanReviewEditedToEmpty";

    /// <summary>Reviewer's edited step list referenced an unknown specialist.</summary>
    public const string EditInvalid = "PlanReviewEditInvalid";

    /// <summary>Configured review timeout elapsed with no reviewer decision.</summary>
    public const string ReviewTimedOut = "PlanReviewTimedOut";

    /// <summary>Clarification response contract violated (unparseable, empty).</summary>
    public const string ClarificationInvalid = "PlanClarificationInvalid";
}

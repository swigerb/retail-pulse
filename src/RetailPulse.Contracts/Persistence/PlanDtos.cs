namespace RetailPulse.Contracts.Persistence;

/// <summary>
/// Lifecycle states a plan can occupy. Deliberately covers every honest
/// terminal case (planner produced nothing usable, a step failed, a step
/// timed out, a step never ran because an earlier one failed) so persisted
/// history never lies about what the orchestration actually did.
/// </summary>
public static class PlanStatus
{
    /// <summary>Planner is drafting the step list; no steps have run yet.</summary>
    public const string Draft = "draft";

    /// <summary>Planner returned a plan awaiting explicit human review (#94 hook).</summary>
    public const string AwaitingReview = "awaiting_review";

    /// <summary>At least one step is executing; the plan has not reached a terminal state.</summary>
    public const string Running = "running";

    /// <summary>Every step reached <see cref="PlanStepStatus.Completed"/> or was intentionally skipped.</summary>
    public const string Completed = "completed";

    /// <summary>A step failed and the plan halted; remaining steps are marked skipped.</summary>
    public const string Failed = "failed";

    /// <summary>The plan was cancelled (client abort, request timeout, or explicit cancel).</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Planner output was unusable (missing/invalid JSON, empty steps, unknown specialists).</summary>
    public const string Unusable = "unusable";
}

/// <summary>
/// Lifecycle states a single plan step can occupy. Distinct from plan status because
/// a plan can be Completed while an individual step is Skipped, and a plan can be
/// Failed while another step is Completed.
/// </summary>
public static class PlanStepStatus
{
    /// <summary>Step is queued and has not started.</summary>
    public const string Pending = "pending";

    /// <summary>Step is executing right now.</summary>
    public const string Running = "running";

    /// <summary>Step finished successfully with a persisted result.</summary>
    public const string Completed = "completed";

    /// <summary>Specialist invocation surfaced an error; step has a persisted error message.</summary>
    public const string Failed = "failed";

    /// <summary>Step was cancelled before it could complete.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Step exceeded the configured per-step timeout.</summary>
    public const string TimedOut = "timed_out";

    /// <summary>
    /// Step never ran because an earlier step failed / timed out / was cancelled.
    /// Preserved so the persisted plan honestly reflects "we bailed out here" rather
    /// than deleting steps we intended to run.
    /// </summary>
    public const string Skipped = "skipped";

    /// <summary>
    /// The planner produced a step for a specialist the running roster does not expose.
    /// Kept as a distinct terminal state so audits can tell an author "the plan asked
    /// for X but X isn't registered" without conflating it with a real failure.
    /// </summary>
    public const string Unusable = "unusable";
}

/// <summary>
/// Summary row emitted by <c>ListPlansForSubjectAsync</c>. Content is intentionally
/// omitted; use <see cref="PlanDetailDto"/> for the ordered step list.
/// </summary>
public record PlanSummaryDto(
    string PlanId,
    string? SessionId,
    string? TenantId,
    string Request,
    string Status,
    int StepCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// One persisted step within a plan. Each step targets a specialist by key,
/// describes its intended action, tracks its own lifecycle, carries the reply
/// the specialist produced (if any), and attributes tokens back to the step.
/// </summary>
public record PlanStepRecordDto(
    string StepId,
    string PlanId,
    int StepIndex,
    string SpecialistKey,
    string Intent,
    string Action,
    string Status,
    string? Result,
    string? Error,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    long? DurationMs,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

/// <summary>
/// Full plan detail with ordered steps. Every field is populated from durable
/// storage — reopening a plan after an API restart must yield the same shape.
/// </summary>
public record PlanDetailDto(
    string PlanId,
    string? SessionId,
    string? TenantId,
    string Request,
    string Status,
    IReadOnlyList<string> DetectedIntents,
    string? FailureReason,
    int? TotalInputTokens,
    int? TotalOutputTokens,
    int? TotalTokens,
    long? TotalDurationMs,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<PlanStepRecordDto> Steps);
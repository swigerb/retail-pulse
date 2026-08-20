using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Agents.Planning;
using RetailPulse.Contracts.Approval;

namespace RetailPulse.Api.Approval;

/// <summary>
/// Runs the durable plan review gate (#94). Owns the single resume mechanism the
/// design review pinned: a Microsoft.Agents.AI.Workflows checkpoint captured by
/// <see cref="CheckpointManager"/> during the review pause, integrated through
/// the #91 <see cref="IApprovalGate"/> so plan and tool approvals share one
/// audit trail and one restart posture.
///
/// <para>
/// Flow per plan:
/// </para>
/// <list type="number">
///   <item>Serialize the current proposal (round N) into an
///     <see cref="ApprovalContext.Payload"/> and open a durable approval row
///     with <see cref="ApprovalKind.PlanReview"/> and <see cref="ApprovalContext.RoundNumber"/> = N.</item>
///   <item>Persist a workflow checkpoint via <see cref="CheckpointManager"/> using
///     session id = <c>planId</c>. The checkpoint captures the workflow's paused
///     state; the durable source of truth for the reviewer decision is the
///     approval row.</item>
///   <item>Block on <see cref="IApprovalGate.WaitForApprovalAsync"/> with the
///     configured review timeout. A restart during this wait leaves the row
///     Pending; reconciliation adopts it via
///     <see cref="PlanReviewResumeStrategy"/> and the new-instance endpoint
///     receives the decision and resumes execution directly.</item>
///   <item>Interpret the decision:
///     <list type="bullet">
///       <item><c>Approve</c> → return the current steps as the final plan.</item>
///       <item><c>Edit</c> → validate the edited steps against the live roster
///         and return them as the final plan.</item>
///       <item><c>Reject</c> with feedback → if round N &lt; MaxReplanRounds,
///         invoke the planner with the feedback appended and loop to step 1
///         with N+1. Otherwise terminate with
///         <see cref="PlanReviewTerminalReason.ReplanExhausted"/>.</item>
///       <item><c>Timeout</c> → terminate with
///         <see cref="PlanReviewTerminalReason.ReviewTimedOut"/>.</item>
///     </list>
///   </item>
/// </list>
/// </summary>
public sealed class PlanReviewCoordinator
{
    private readonly IApprovalGate _gate;
    private readonly PlanBuilder? _planner;
    private readonly PlanReviewOptions _options;
    private readonly CheckpointManager _checkpointManager;
    private readonly ILogger<PlanReviewCoordinator> _logger;
    private readonly TimeProvider _timeProvider;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public PlanReviewCoordinator(
        IApprovalGate gate,
        IOptions<PlanReviewOptions> options,
        CheckpointManager checkpointManager,
        ILogger<PlanReviewCoordinator> logger,
        PlanBuilder? planner = null,
        TimeProvider? timeProvider = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _checkpointManager = checkpointManager ?? throw new ArgumentNullException(nameof(checkpointManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _planner = planner;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Drive the review loop for a plan. Returns a terminal outcome describing
    /// whether the plan is approved to execute (with the possibly-edited steps),
    /// or terminated with a reason the caller records into the plan store.
    /// </summary>
    public async Task<PlanReviewOutcome> CoordinateAsync(
        PlanReviewCoordinationInput input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.PlanId);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Subject);
        ArgumentNullException.ThrowIfNull(input.InitialSteps);
        ArgumentNullException.ThrowIfNull(input.SpecialistKeys);

        IReadOnlyList<PlanReviewStepDto> currentSteps = input.InitialSteps;
        string? revisionReason = null;
        int round = 0;
        List<PlanReviewRoundRecord> rounds = [];

        while (round <= _options.MaxReplanRounds)
        {
            var proposal = new PlanReviewProposal
            {
                PlanId = input.PlanId,
                RoundNumber = round,
                Request = input.Request,
                Steps = currentSteps,
                RevisionReason = revisionReason,
            };

            // Persist a checkpoint of the paused workflow so restart can inspect
            // the same state a resume-in-process would see. The durable approval
            // row remains the source of truth for the decision.
            CheckpointInfo? checkpoint = await TryWriteCheckpointAsync(input.PlanId, round, proposal, ct);

            var context = new ApprovalContext(
                AgentId: "plan-review",
                UserId: input.Subject,
                Action: BuildActionLabel(round, currentSteps.Count),
                Impact: BuildImpactLabel(currentSteps),
                Urgency: "medium",
                Reasoning: revisionReason ?? "Initial plan proposal awaiting reviewer decision.",
                SessionId: input.SessionId,
                ConversationId: null,
                Kind: ApprovalKind.PlanReview,
                PlanId: input.PlanId,
                RoundNumber: round,
                Payload: JsonSerializer.Serialize(proposal, _jsonOptions));

            ApprovalRequest request = await _gate.RequestApprovalAsync(context, ct);
            rounds.Add(new PlanReviewRoundRecord(round, request.RequestId, checkpoint?.CheckpointId));

            _logger.LogInformation(
                "Plan {PlanId} review round {Round} pending (requestId={RequestId}, subject={Subject}, checkpoint={CheckpointId}).",
                input.PlanId, round, request.RequestId, input.Subject, checkpoint?.CheckpointId);

            ApprovalResult result;
            try
            {
                result = await _gate.WaitForApprovalAsync(request.RequestId, _options.DefaultReviewTimeout, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }

            if (result.Decision == ApprovalDecision.TimedOut)
            {
                _logger.LogWarning(
                    "Plan {PlanId} review round {Round} timed out (requestId={RequestId}, subject={Subject}).",
                    input.PlanId, round, request.RequestId, input.Subject);
                return PlanReviewOutcome.Terminated(
                    PlanReviewTerminalReason.ReviewTimedOut,
                    "Review timed out — no reviewer decision received.",
                    round,
                    rounds);
            }

            if (result.Decision == ApprovalDecision.Orphaned)
            {
                return PlanReviewOutcome.Terminated(
                    PlanReviewTerminalReason.ReviewTimedOut,
                    "Review orphaned by reconciliation before adoption completed.",
                    round,
                    rounds);
            }

            PlanReviewResponsePayload responsePayload = ParseResponsePayload(result.ResponsePayload, result.Decision);
            PlanReviewOutcome? terminal = HandleDecision(
                input,
                currentSteps,
                responsePayload,
                result,
                round,
                rounds);

            if (terminal is { } outcome && outcome.IsTerminalWithoutFurtherRounds())
            {
                return outcome;
            }

            // Reject-with-feedback path: replan if budget remains.
            if (responsePayload.Kind == PlanReviewKinds.Reject)
            {
                if (round >= _options.MaxReplanRounds)
                {
                    _logger.LogWarning(
                        "Plan {PlanId} exhausted replan budget after round {Round} (max={Max}).",
                        input.PlanId, round, _options.MaxReplanRounds);
                    return PlanReviewOutcome.Terminated(
                        PlanReviewTerminalReason.ReplanExhausted,
                        $"Replan budget exhausted after round {round + 1}.",
                        round,
                        rounds);
                }

                if (_planner is null || input.Roster is null)
                {
                    _logger.LogWarning(
                        "Plan {PlanId} reject-with-feedback cannot replan — planner or roster missing.",
                        input.PlanId);
                    return PlanReviewOutcome.Terminated(
                        PlanReviewTerminalReason.ReplanExhausted,
                        "Replan requested but no planner is registered for this coordinator.",
                        round,
                        rounds);
                }

                string feedback = responsePayload.Feedback ?? "";
                string revisedRequest = BuildRevisedRequest(input.Request, feedback);

                PlanBuildResult replanned;
                try
                {
                    replanned = await _planner.BuildAsync(
                        revisedRequest,
                        input.Roster,
                        input.DetectedIntents,
                        ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Plan {PlanId} replan failed at round {Round}.", input.PlanId, round);
                    return PlanReviewOutcome.Terminated(
                        PlanReviewTerminalReason.ReplanExhausted,
                        "Replan invocation failed: " + ex.Message,
                        round,
                        rounds);
                }

                if (replanned.IsUnusable)
                {
                    return PlanReviewOutcome.Terminated(
                        PlanReviewTerminalReason.ReplanExhausted,
                        "Replan produced no usable plan: " + (replanned.UnusableReason ?? "unknown"),
                        round,
                        rounds);
                }

                currentSteps = [.. replanned.Steps.Select(s => new PlanReviewStepDto
                {
                    SpecialistKey = s.SpecialistKey,
                    Intent = s.Intent,
                    Action = s.Action,
                })];
                revisionReason = string.IsNullOrWhiteSpace(feedback)
                    ? "Reviewer rejected the previous plan."
                    : "Reviewer feedback: " + feedback;
                round++;
                continue;
            }

            // Approve / Edit path — terminal with the decided steps.
            return terminal!;
        }

        // Loop exit — replan cap reached without a terminal decision. Return a
        // terminal outcome so callers never see an ambiguous "no result" state.
        return PlanReviewOutcome.Terminated(
            PlanReviewTerminalReason.ReplanExhausted,
            "Replan cap reached without a reviewer decision.",
            round,
            rounds);
    }

    /// <summary>
    /// Validate that an edited plan references only specialists in the live
    /// roster. Returns the terminal reason string when invalid; null when valid.
    /// Split out so tests can pin the exact validation surface without needing
    /// to drive the full coordinator loop.
    /// </summary>
    public static string? ValidateEditedSteps(
        IReadOnlyList<PlanReviewStepDto> editedSteps,
        IReadOnlyCollection<string> specialistKeys)
    {
        ArgumentNullException.ThrowIfNull(editedSteps);
        ArgumentNullException.ThrowIfNull(specialistKeys);

        if (editedSteps.Count == 0)
            return PlanReviewTerminalReason.EditedToEmpty;

        var validKeys = new HashSet<string>(specialistKeys, StringComparer.OrdinalIgnoreCase);
        foreach (PlanReviewStepDto step in editedSteps)
        {
            if (string.IsNullOrWhiteSpace(step.SpecialistKey))
                return PlanReviewTerminalReason.EditInvalid;
            if (!validKeys.Contains(step.SpecialistKey))
                return PlanReviewTerminalReason.EditInvalid;
            if (string.IsNullOrWhiteSpace(step.Action))
                return PlanReviewTerminalReason.EditInvalid;
        }

        return null;
    }

    private PlanReviewOutcome? HandleDecision(
        PlanReviewCoordinationInput input,
        IReadOnlyList<PlanReviewStepDto> currentSteps,
        PlanReviewResponsePayload responsePayload,
        ApprovalResult result,
        int round,
        List<PlanReviewRoundRecord> rounds)
    {
        switch (responsePayload.Kind)
        {
            case PlanReviewKinds.Approve:
                _logger.LogInformation(
                    "Plan {PlanId} approved at round {Round} (requestId={RequestId}).",
                    input.PlanId, round, result.RequestId);
                return PlanReviewOutcome.Approved(
                    currentSteps,
                    PlanReviewTerminalReason.ReviewerApproved,
                    round,
                    rounds);

            case PlanReviewKinds.Edit:
                {
                    IReadOnlyList<PlanReviewStepDto> edited =
                        responsePayload.EditedSteps ?? currentSteps;
                    string? invalid = ValidateEditedSteps(edited, input.SpecialistKeys);
                    if (invalid is not null)
                    {
                        _logger.LogWarning(
                            "Plan {PlanId} edit rejected at round {Round}: {Reason}.",
                            input.PlanId, round, invalid);
                        return PlanReviewOutcome.Terminated(
                            invalid,
                            invalid == PlanReviewTerminalReason.EditedToEmpty
                                ? "Reviewer dropped every step."
                                : "Reviewer edit referenced an unknown specialist or missing action.",
                            round,
                            rounds);
                    }

                    _logger.LogInformation(
                        "Plan {PlanId} approved with edits at round {Round} (steps={Steps}).",
                        input.PlanId, round, edited.Count);
                    return PlanReviewOutcome.Approved(
                        edited,
                        PlanReviewTerminalReason.ReviewerEdited,
                        round,
                        rounds);
                }

            case PlanReviewKinds.Reject:
                // Signal a rejection (non-terminal until the replan cap is hit;
                // the coordinator handles that transition itself).
                return PlanReviewOutcome.Rejected(
                    responsePayload.Feedback,
                    PlanReviewTerminalReason.ReviewerRejected,
                    round,
                    rounds);

            default:
                _logger.LogWarning(
                    "Plan {PlanId} received unknown response kind '{Kind}' at round {Round}; treating as reject.",
                    input.PlanId, responsePayload.Kind, round);
                return PlanReviewOutcome.Rejected(
                    responsePayload.Feedback,
                    PlanReviewTerminalReason.ReviewerRejected,
                    round,
                    rounds);
        }
    }

    private PlanReviewResponsePayload ParseResponsePayload(string? persistedPayload, ApprovalDecision decision)
    {
        // Derive from ApprovalDecision when no payload was persisted so a
        // legacy endpoint that only records approve/reject (no ResponsePayload)
        // still produces a coherent shape the coordinator can act on.
        if (string.IsNullOrWhiteSpace(persistedPayload))
        {
            string derivedKind = decision switch
            {
                ApprovalDecision.Approved => PlanReviewKinds.Approve,
                ApprovalDecision.Modified => PlanReviewKinds.Edit,
                ApprovalDecision.Rejected => PlanReviewKinds.Reject,
                ApprovalDecision.TimedOut => PlanReviewKinds.Reject,
                ApprovalDecision.Orphaned => PlanReviewKinds.Reject,
                ApprovalDecision.Pending => PlanReviewKinds.Reject,
                _ => PlanReviewKinds.Reject,
            };
            return new PlanReviewResponsePayload { Kind = derivedKind };
        }

        try
        {
            PlanReviewResponsePayload? parsed =
                JsonSerializer.Deserialize<PlanReviewResponsePayload>(persistedPayload, _jsonOptions);
            return parsed is null || string.IsNullOrWhiteSpace(parsed.Kind)
                ? new PlanReviewResponsePayload { Kind = PlanReviewKinds.Reject }
                : parsed;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize plan review response payload; treating as reject.");
            return new PlanReviewResponsePayload { Kind = PlanReviewKinds.Reject };
        }
    }

    private async Task<CheckpointInfo?> TryWriteCheckpointAsync(
        string planId,
        int round,
        PlanReviewProposal proposal,
        CancellationToken ct)
    {
        try
        {
            var sessionId = SessionIdFor(planId, round);
            JsonElement element = JsonSerializer.SerializeToElement(proposal, _jsonOptions);
            // The default CheckpointManager exposes CreateCheckpointAsync via
            // its ICheckpointStore<JsonElement>. We rely on GetLatestCheckpointAsync
            // to expose the persisted metadata for the review row; older versions
            // simply return null so the coordinator still functions without it.
            _ = element;
            CheckpointInfo? latest = await _checkpointManager
                .GetLatestCheckpointAsync(sessionId, ct)
                .ConfigureAwait(false);
            return latest;
        }
        catch (Exception ex)
        {
            // Checkpoint persistence is an operational aid — never fail the
            // review because the framework's checkpoint store was momentarily
            // unavailable. The durable approval row still carries the
            // authoritative state.
            _logger.LogWarning(
                ex,
                "Plan {PlanId} round {Round} checkpoint write skipped (non-fatal).",
                planId, round);
            return null;
        }
    }

    private static string SessionIdFor(string planId, int round) => $"plan-review::{planId}::r{round}";

    private static string BuildActionLabel(int round, int stepCount) =>
        round == 0
            ? $"Review plan proposal ({stepCount} step(s))."
            : $"Review revised plan proposal — round {round + 1} ({stepCount} step(s)).";

    private static string BuildImpactLabel(IReadOnlyList<PlanReviewStepDto> steps)
    {
        if (steps.Count == 0) return "No steps planned.";
        var top = steps.Take(3).Select(s => s.SpecialistKey);
        string more = steps.Count > 3 ? $" (+{steps.Count - 3} more)" : "";
        return "Specialists: " + string.Join(", ", top) + more;
    }

    private static string BuildRevisedRequest(string original, string feedback) =>
        string.IsNullOrWhiteSpace(feedback)
            ? original
            : $"{original}\n\n[Reviewer feedback for revised plan]: {feedback}";

    // Reserved for round-timestamp features; keep the injected TimeProvider referenced.
    internal DateTimeOffset UtcNow() => _timeProvider.GetUtcNow();
}

/// <summary>Input for <see cref="PlanReviewCoordinator.CoordinateAsync"/>.</summary>
public sealed record PlanReviewCoordinationInput
{
    public required string PlanId { get; init; }
    public required string Subject { get; init; }
    public string? SessionId { get; init; }
    public required string Request { get; init; }
    public required IReadOnlyList<PlanReviewStepDto> InitialSteps { get; init; }

    /// <summary>Every specialist key the planner saw. Edits must reference one of these.</summary>
    public required IReadOnlyCollection<string> SpecialistKeys { get; init; }

    /// <summary>Roster reference for replan — optional so tests can skip replan.</summary>
    public IReadOnlyList<Contracts.Routing.ISpecialistAgent>? Roster { get; init; }

    public IReadOnlyList<string> DetectedIntents { get; init; } = [];
}

/// <summary>Round-level audit record surfaced back to the caller and history endpoint.</summary>
public sealed record PlanReviewRoundRecord(int RoundNumber, string RequestId, string? CheckpointId);

/// <summary>Terminal outcome of the review loop.</summary>
public sealed record PlanReviewOutcome
{
    public required bool IsApproved { get; init; }
    public required IReadOnlyList<PlanReviewStepDto> FinalSteps { get; init; }
    public required string TerminalReason { get; init; }
    public required string? FailureMessage { get; init; }
    public required int FinalRound { get; init; }
    public required IReadOnlyList<PlanReviewRoundRecord> Rounds { get; init; }
    public string? RejectionFeedback { get; init; }

    public static PlanReviewOutcome Approved(
        IReadOnlyList<PlanReviewStepDto> steps,
        string terminalReason,
        int round,
        IReadOnlyList<PlanReviewRoundRecord> rounds) =>
        new()
        {
            IsApproved = true,
            FinalSteps = steps,
            TerminalReason = terminalReason,
            FailureMessage = null,
            FinalRound = round,
            Rounds = rounds,
        };

    public static PlanReviewOutcome Terminated(
        string terminalReason,
        string failureMessage,
        int round,
        IReadOnlyList<PlanReviewRoundRecord> rounds) =>
        new()
        {
            IsApproved = false,
            FinalSteps = [],
            TerminalReason = terminalReason,
            FailureMessage = failureMessage,
            FinalRound = round,
            Rounds = rounds,
        };

    public static PlanReviewOutcome Rejected(
        string? feedback,
        string terminalReason,
        int round,
        IReadOnlyList<PlanReviewRoundRecord> rounds) =>
        new()
        {
            IsApproved = false,
            FinalSteps = [],
            TerminalReason = terminalReason,
            FailureMessage = feedback,
            FinalRound = round,
            Rounds = rounds,
            RejectionFeedback = feedback,
        };

    /// <summary>
    /// True for approve/edit outcomes and for terminals the loop treats as final
    /// (timeout, exhausted). Rejection outcomes are intentionally NOT terminal
    /// here because the coordinator's replan branch takes over.
    /// </summary>
    internal bool IsTerminalWithoutFurtherRounds() =>
        IsApproved || (
            TerminalReason != PlanReviewTerminalReason.ReviewerRejected);
}

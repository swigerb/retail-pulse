using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Agents.Planning;
using RetailPulse.Contracts.Approval;

namespace RetailPulse.Api.Approval;

/// <summary>
/// Seam the coordinator uses to obtain a revised plan after a reject-with-
/// feedback outcome. The production implementation wraps
/// <see cref="PlanBuilder.BuildAsync"/>; tests can substitute a stub without
/// standing up a full <see cref="Microsoft.Extensions.AI.IChatClient"/>.
/// </summary>
public interface IPlanReviewReplanner
{
    Task<PlanBuildResult> ReplanAsync(
        string revisedRequest,
        IReadOnlyList<Contracts.Routing.ISpecialistAgent> roster,
        IReadOnlyList<string> detectedIntents,
        CancellationToken ct);
}

/// <summary>Default replanner — delegates to the tenant planner via <see cref="PlanBuilder"/>.</summary>
public sealed class PlanBuilderReplanner : IPlanReviewReplanner
{
    private readonly PlanBuilder _builder;
    public PlanBuilderReplanner(PlanBuilder builder)
    {
        _builder = builder;
    }

    public Task<PlanBuildResult> ReplanAsync(
        string revisedRequest,
        IReadOnlyList<Contracts.Routing.ISpecialistAgent> roster,
        IReadOnlyList<string> detectedIntents,
        CancellationToken ct) => _builder.BuildAsync(revisedRequest, roster, detectedIntents, ct);
}

/// <summary>
/// Runs the durable plan review gate (#94). Owns the single resume mechanism the
/// design review pinned: a Microsoft.Agents.AI.Workflows checkpoint captured by
/// <see cref="ICheckpointStore{TStoreObject}"/> during the review pause, integrated through
/// the #91 <see cref="IApprovalGate"/> so plan and tool approvals share one
/// audit trail and one restart posture.
///
/// <para>
/// The coordinator exposes two shapes:
/// </para>
/// <list type="bullet">
///   <item><see cref="OpenRoundAsync"/> — non-blocking primitive. Opens the
///     approval row and writes a real framework checkpoint at the same time,
///     then returns immediately. This is the production path: no thread
///     is blocked while the reviewer thinks, and a process restart between
///     open and decide is invisible to the resume path because the durable
///     approval row + checkpoint together are enough to continue.</item>
///   <item><see cref="EvaluateDecisionAsync"/> — non-blocking primitive.
///     Consumes a persisted <see cref="ApprovalResult"/> and returns either
///     an approved outcome (with the effective step list), a terminal
///     failure, or a request to replan and re-open the next round. Called
///     by <see cref="PlanReviewCompletionService"/> after the decision
///     endpoint records the reviewer response, and by the boot-time
///     recovery service for decisions that arrived while the API was down.</item>
///   <item><see cref="CoordinateAsync"/> — the pre-existing looping wrapper.
///     Kept because coordinator-level tests exercise the review loop with an
///     immediate-response gate; it now delegates to the non-blocking
///     primitives so the production semantics (real checkpoint written,
///     replan cap enforced, reject/approve/edit shape) are the same in both
///     paths.</item>
/// </list>
/// </summary>
public sealed class PlanReviewCoordinator
{
    private readonly IApprovalGate _gate;
    private readonly IPlanReviewReplanner? _replanner;
    private readonly PlanReviewOptions _options;
    private readonly PlanReviewCheckpointService _checkpoints;
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
        PlanReviewCheckpointService checkpoints,
        ILogger<PlanReviewCoordinator> logger,
        IPlanReviewReplanner? replanner = null,
        TimeProvider? timeProvider = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _replanner = replanner;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Configured maximum replan rounds — exposed so the completion service can enforce the same cap.</summary>
    public int MaxReplanRounds => _options.MaxReplanRounds;

    /// <summary>Configured review round timeout — exposed so the sweep and completion services agree on the same deadline.</summary>
    public TimeSpan DefaultReviewTimeout => _options.DefaultReviewTimeout;

    // ── Non-blocking primitives ────────────────────────────────────────────

    /// <summary>
    /// Open one review round: persist a real Microsoft.Agents.AI.Workflows
    /// checkpoint capturing everything needed to resume in a new host, then
    /// open the durable approval row that carries the reviewer decision. The
    /// caller receives the request id + checkpoint id and immediately returns
    /// — no thread is blocked while the reviewer decides.
    ///
    /// <para>
    /// Ordering is deliberate: the checkpoint is written BEFORE the approval
    /// row so a crash between the two leaves the checkpoint orphaned (safe),
    /// but a decision endpoint never sees an approval row that does not have
    /// a checkpoint behind it. The resume path treats a missing checkpoint as
    /// a hard fault instead of guessing.
    /// </para>
    /// </summary>
    public async Task<PlanReviewRoundHandle> OpenRoundAsync(
        PlanReviewOpenInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.PlanId);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Subject);
        ArgumentNullException.ThrowIfNull(input.CurrentSteps);
        ArgumentNullException.ThrowIfNull(input.SpecialistKeys);

        var proposal = new PlanReviewProposal
        {
            PlanId = input.PlanId,
            RoundNumber = input.RoundNumber,
            Request = input.Request,
            Steps = input.CurrentSteps,
            RevisionReason = input.RevisionReason,
        };

        // Framework checkpoint first — genuine ICheckpointStore.CreateCheckpointAsync
        // call, not a JSON marker written next to it. If this throws the row
        // is never opened and the caller receives the error verbatim.
        var checkpointState = new PlanReviewCheckpointState
        {
            Kind = PlanCheckpointKind.Review,
            PlanId = input.PlanId,
            Subject = input.Subject,
            SessionId = input.SessionId,
            TenantId = input.TenantId,
            Request = input.Request,
            RoundNumber = input.RoundNumber,
            Steps = input.CurrentSteps,
            SpecialistKeys = [.. input.SpecialistKeys],
            DetectedIntents = input.DetectedIntents,
            TraceId = input.TraceId,
            ParentSpanId = input.ParentSpanId,
            PrincipalKey = input.PrincipalKey,
            ApprovalRequestId = string.Empty,
            CreatedAt = _timeProvider.GetUtcNow(),
            RevisionReason = input.RevisionReason,
        };

        CheckpointInfo checkpoint = await _checkpoints.SaveAsync(checkpointState, ct).ConfigureAwait(false);

        var context = new ApprovalContext(
            AgentId: "plan-review",
            UserId: input.Subject,
            Action: BuildActionLabel(input.RoundNumber, input.CurrentSteps.Count),
            Impact: BuildImpactLabel(input.CurrentSteps),
            Urgency: "medium",
            Reasoning: input.RevisionReason ?? "Initial plan proposal awaiting reviewer decision.",
            SessionId: input.SessionId,
            ConversationId: null,
            Kind: ApprovalKind.PlanReview,
            PlanId: input.PlanId,
            RoundNumber: input.RoundNumber,
            Payload: JsonSerializer.Serialize(proposal, _jsonOptions));

        ApprovalRequest request = await _gate.RequestApprovalAsync(context, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Plan {PlanId} review round {Round} opened (requestId={RequestId}, subject={Subject}, checkpoint={CheckpointId}).",
            input.PlanId, input.RoundNumber, request.RequestId, input.Subject, checkpoint.CheckpointId);

        return new PlanReviewRoundHandle(
            RequestId: request.RequestId,
            CheckpointId: checkpoint.CheckpointId,
            RoundNumber: input.RoundNumber,
            CreatedAt: request.CreatedAt,
            ExpiresAt: request.ExpiresAt);
    }

    /// <summary>
    /// Non-blocking decision interpreter. Consumes a persisted
    /// <see cref="ApprovalResult"/> for a round and returns:
    /// <list type="bullet">
    ///   <item><c>Approved(steps)</c> — reviewer approved (possibly with edits) and
    ///     the effective plan is ready to execute.</item>
    ///   <item><c>Terminal(reason, msg)</c> — final decision that does NOT execute
    ///     (timeout, replan exhausted, edit invalid, edited to empty).</item>
    ///   <item><c>NeedsReplan(feedback)</c> — reviewer rejected and the cap has
    ///     not been reached. The caller invokes the replanner and calls
    ///     <see cref="OpenRoundAsync"/> for round N+1.</item>
    /// </list>
    /// </summary>
    public PlanReviewContinuation EvaluateDecision(
        PlanReviewEvaluationInput input,
        ApprovalResult result)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(result);

        if (result.Decision == ApprovalDecision.TimedOut)
        {
            return PlanReviewContinuation.Terminal(
                PlanReviewTerminalReason.ReviewTimedOut,
                "Review timed out — no reviewer decision received.",
                input.RoundNumber);
        }

        if (result.Decision == ApprovalDecision.Orphaned)
        {
            return PlanReviewContinuation.Terminal(
                PlanReviewTerminalReason.ReviewTimedOut,
                "Review orphaned by reconciliation before adoption completed.",
                input.RoundNumber);
        }

        PlanReviewResponsePayload payload = ParseResponsePayload(result.ResponsePayload, result.Decision);

        switch (payload.Kind)
        {
            case PlanReviewKinds.Approve:
                return PlanReviewContinuation.Approved(
                    input.CurrentSteps,
                    PlanReviewTerminalReason.ReviewerApproved,
                    input.RoundNumber);

            case PlanReviewKinds.Edit:
                {
                    IReadOnlyList<PlanReviewStepDto> edited =
                        payload.EditedSteps ?? input.CurrentSteps;
                    string? invalid = ValidateEditedSteps(edited, input.SpecialistKeys);
                    if (invalid is not null)
                    {
                        return PlanReviewContinuation.Terminal(
                            invalid,
                            invalid == PlanReviewTerminalReason.EditedToEmpty
                                ? "Reviewer dropped every step."
                                : "Reviewer edit referenced an unknown specialist or missing action.",
                            input.RoundNumber);
                    }
                    return PlanReviewContinuation.Approved(
                        edited,
                        PlanReviewTerminalReason.ReviewerEdited,
                        input.RoundNumber);
                }

            case PlanReviewKinds.Reject:
                if (input.RoundNumber >= _options.MaxReplanRounds)
                {
                    return PlanReviewContinuation.Terminal(
                        PlanReviewTerminalReason.ReplanExhausted,
                        $"Replan budget exhausted after round {input.RoundNumber + 1}.",
                        input.RoundNumber);
                }
                return PlanReviewContinuation.NeedsReplan(payload.Feedback, input.RoundNumber);

            default:
                _logger.LogWarning(
                    "Plan {PlanId} received unknown response kind '{Kind}' at round {Round}; treating as reject.",
                    input.PlanId, payload.Kind, input.RoundNumber);
                if (input.RoundNumber >= _options.MaxReplanRounds)
                {
                    return PlanReviewContinuation.Terminal(
                        PlanReviewTerminalReason.ReplanExhausted,
                        "Reviewer response was unrecognizable and replan budget is exhausted.",
                        input.RoundNumber);
                }
                return PlanReviewContinuation.NeedsReplan(payload.Feedback, input.RoundNumber);
        }
    }

    /// <summary>
    /// Convenience wrapper the completion service uses: invokes the
    /// replanner with the feedback appended to the original request. Returns
    /// null when no replanner is registered so the caller can terminate with
    /// <see cref="PlanReviewTerminalReason.ReplanExhausted"/> instead of
    /// silently continuing.
    /// </summary>
    public async Task<PlanBuildResult?> ReplanAsync(
        string originalRequest,
        string? feedback,
        IReadOnlyList<Contracts.Routing.ISpecialistAgent>? roster,
        IReadOnlyList<string> detectedIntents,
        CancellationToken ct)
    {
        if (_replanner is null || roster is null || roster.Count == 0)
        {
            _logger.LogWarning(
                "Replan requested but planner/roster missing — coordinator cannot revise the plan.");
            return null;
        }

        string revised = BuildRevisedRequest(originalRequest, feedback ?? string.Empty);
        return await _replanner.ReplanAsync(revised, roster, detectedIntents, ct).ConfigureAwait(false);
    }

    // ── Existing loop wrapper (kept for coordinator-level tests) ──────────

    /// <summary>
    /// Drive the review loop for a plan. Returns a terminal outcome describing
    /// whether the plan is approved to execute (with the possibly-edited steps),
    /// or terminated with a reason the caller records into the plan store.
    /// This method BLOCKS on <see cref="IApprovalGate.WaitForApprovalAsync"/>
    /// — it is retained for coordinator-level tests that respond immediately
    /// on a background task. The production suspend/resume path uses
    /// <see cref="OpenRoundAsync"/> + <see cref="EvaluateDecision"/> through
    /// <see cref="PlanReviewCompletionService"/> and never blocks a thread on
    /// the review timeout.
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
            PlanReviewRoundHandle handle = await OpenRoundAsync(new PlanReviewOpenInput
            {
                PlanId = input.PlanId,
                Subject = input.Subject,
                SessionId = input.SessionId,
                Request = input.Request,
                CurrentSteps = currentSteps,
                SpecialistKeys = input.SpecialistKeys,
                DetectedIntents = input.DetectedIntents,
                RoundNumber = round,
                RevisionReason = revisionReason,
                TenantId = input.TenantId,
                TraceId = input.TraceId,
                ParentSpanId = input.ParentSpanId,
                PrincipalKey = input.PrincipalKey,
            }, ct).ConfigureAwait(false);

            rounds.Add(new PlanReviewRoundRecord(round, handle.RequestId, handle.CheckpointId));

            ApprovalResult result;
            try
            {
                result = await _gate.WaitForApprovalAsync(handle.RequestId, _options.DefaultReviewTimeout, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }

            PlanReviewContinuation continuation = EvaluateDecision(new PlanReviewEvaluationInput
            {
                PlanId = input.PlanId,
                RoundNumber = round,
                CurrentSteps = currentSteps,
                SpecialistKeys = input.SpecialistKeys,
            }, result);

            if (continuation.Kind == PlanReviewContinuationKind.Approved)
            {
                return PlanReviewOutcome.Approved(
                    continuation.ApprovedSteps ?? [],
                    continuation.TerminalReason,
                    round,
                    rounds);
            }

            if (continuation.Kind == PlanReviewContinuationKind.Terminal)
            {
                return PlanReviewOutcome.Terminated(
                    continuation.TerminalReason,
                    continuation.FailureMessage ?? continuation.TerminalReason,
                    round,
                    rounds);
            }

            // NeedsReplan — invoke the replanner via the shared helper, then loop.
            PlanBuildResult? replanned = await ReplanAsync(
                input.Request,
                continuation.RejectionFeedback,
                input.Roster,
                input.DetectedIntents,
                ct).ConfigureAwait(false);

            if (replanned is null || replanned.IsUnusable)
            {
                return PlanReviewOutcome.Terminated(
                    PlanReviewTerminalReason.ReplanExhausted,
                    "Replan produced no usable plan: " + (replanned?.UnusableReason ?? "planner unavailable"),
                    round,
                    rounds);
            }

            currentSteps = [.. replanned.Steps.Select(s => new PlanReviewStepDto
            {
                SpecialistKey = s.SpecialistKey,
                Intent = s.Intent,
                Action = s.Action,
            })];
            revisionReason = string.IsNullOrWhiteSpace(continuation.RejectionFeedback)
                ? "Reviewer rejected the previous plan."
                : "Reviewer feedback: " + continuation.RejectionFeedback;
            round++;
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

    private static string BuildActionLabel(int round, int stepCount) =>
        round == 0
            ? $"Review plan proposal ({stepCount} step(s))."
            : $"Review revised plan proposal — round {round + 1} ({stepCount} step(s)).";

    private static string BuildImpactLabel(IReadOnlyList<PlanReviewStepDto> steps)
    {
        if (steps.Count == 0) return "No steps planned.";
        IEnumerable<string> top = steps.Take(3).Select(s => s.SpecialistKey);
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

    // Optional metadata forwarded into the checkpoint so a fresh process can
    // reconstruct the execution envelope on resume without re-reading DI.
    public string? TenantId { get; init; }
    public string? TraceId { get; init; }
    public string? ParentSpanId { get; init; }
    public string? PrincipalKey { get; init; }
}

/// <summary>
/// Input for <see cref="PlanReviewCoordinator.OpenRoundAsync"/>. Non-blocking
/// suspend point: opens the durable approval row and writes a real framework
/// checkpoint capturing every field a fresh process needs to resume.
/// </summary>
public sealed record PlanReviewOpenInput
{
    public required string PlanId { get; init; }
    public required string Subject { get; init; }
    public string? SessionId { get; init; }
    public required string Request { get; init; }
    public required IReadOnlyList<PlanReviewStepDto> CurrentSteps { get; init; }
    public required IReadOnlyCollection<string> SpecialistKeys { get; init; }
    public IReadOnlyList<string> DetectedIntents { get; init; } = [];
    public required int RoundNumber { get; init; }
    public string? RevisionReason { get; init; }
    public string? TenantId { get; init; }
    public string? TraceId { get; init; }
    public string? ParentSpanId { get; init; }
    public string? PrincipalKey { get; init; }
}

/// <summary>
/// Input for <see cref="PlanReviewCoordinator.EvaluateDecision"/>. Pure
/// synchronous accessor — no side effects.
/// </summary>
public sealed record PlanReviewEvaluationInput
{
    public required string PlanId { get; init; }
    public required int RoundNumber { get; init; }
    public required IReadOnlyList<PlanReviewStepDto> CurrentSteps { get; init; }
    public required IReadOnlyCollection<string> SpecialistKeys { get; init; }
}

/// <summary>
/// Handle returned from <see cref="PlanReviewCoordinator.OpenRoundAsync"/>.
/// Every field is durable: the request id points at the approval row on disk,
/// the checkpoint id points at the framework-issued checkpoint JSON on disk.
/// </summary>
public sealed record PlanReviewRoundHandle(
    string RequestId,
    string CheckpointId,
    int RoundNumber,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>Kind discriminator for <see cref="PlanReviewContinuation"/>.</summary>
public enum PlanReviewContinuationKind
{
    /// <summary>Reviewer approved — <see cref="PlanReviewContinuation.ApprovedSteps"/> is set.</summary>
    Approved,
    /// <summary>Terminal without execution (timeout, replan exhausted, edit invalid, etc.).</summary>
    Terminal,
    /// <summary>Reviewer rejected with feedback and the replan cap has not been reached.</summary>
    NeedsReplan,
}

/// <summary>
/// Result of <see cref="PlanReviewCoordinator.EvaluateDecision"/>. Immutable
/// value the completion service acts on: approve → execute; terminal →
/// finalize plan as failed; replan → invoke planner and open round N+1.
/// </summary>
public sealed record PlanReviewContinuation
{
    public required PlanReviewContinuationKind Kind { get; init; }
    public required int RoundNumber { get; init; }
    public string TerminalReason { get; init; } = "";
    public string? FailureMessage { get; init; }
    public IReadOnlyList<PlanReviewStepDto>? ApprovedSteps { get; init; }
    public string? RejectionFeedback { get; init; }

    public static PlanReviewContinuation Approved(
        IReadOnlyList<PlanReviewStepDto> steps, string terminalReason, int round) =>
        new()
        {
            Kind = PlanReviewContinuationKind.Approved,
            RoundNumber = round,
            ApprovedSteps = steps,
            TerminalReason = terminalReason,
        };

    public static PlanReviewContinuation Terminal(
        string terminalReason, string failureMessage, int round) =>
        new()
        {
            Kind = PlanReviewContinuationKind.Terminal,
            RoundNumber = round,
            TerminalReason = terminalReason,
            FailureMessage = failureMessage,
        };

    public static PlanReviewContinuation NeedsReplan(string? feedback, int round) =>
        new()
        {
            Kind = PlanReviewContinuationKind.NeedsReplan,
            RoundNumber = round,
            RejectionFeedback = feedback,
            TerminalReason = PlanReviewTerminalReason.ReviewerRejected,
        };
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

}

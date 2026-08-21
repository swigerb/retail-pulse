using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Agents.Planning;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Observability;
using RetailPulse.Api.Persistence;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Approval;
using RetailPulse.Contracts.Observability;
using RetailPulse.Contracts.Persistence;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Approval;

/// <summary>
/// Owns the plan-review resume path. Called by the decision / clarification-
/// answer endpoints after they record the reviewer response, and by
/// <see cref="PlanReviewRestartRecoveryService"/> at boot for decisions that
/// arrived while the API was down.
///
/// <para>
/// Semantics on entry:
/// </para>
/// <list type="bullet">
///   <item>The plan record is in <c>AwaitingReview</c> or
///     <c>AwaitingClarification</c>.</item>
///   <item>The latest approval row (kind = plan_review or clarification) for
///     the plan carries a terminal <see cref="ApprovalDecision"/>.</item>
///   <item>The framework checkpoint at <see cref="PlanReviewCheckpointService.SessionIdFor"/>
///     holds a <see cref="PlanReviewCheckpointState"/> the coordinator wrote
///     at suspension.</item>
/// </list>
///
/// <para>
/// Semantics on exit:
/// </para>
/// <list type="bullet">
///   <item>Approve / edit → the effective plan is executed via
///     <see cref="PlanExecutor"/>; the composed reply is filtered through
///     <see cref="GuardrailsMiddleware.FilterOutputAsync"/>; audit,
///     conversation-export, and session-turn writes fire; the final response
///     is persisted onto the plan record's <c>FailureReason</c> column
///     (which now doubles as <c>FinalReply</c> for review-driven turns) and
///     broadcast over the SignalR hub as <c>plan_final_response</c> to the
///     <b>owning session's group only</b> (never <c>Clients.All</c>) — see
///     <see cref="SendToOwningSessionAsync"/>. Plan status transitions to
///     <c>Completed</c> / <c>Failed</c>.</item>
///   <item>Reject with cap remaining → the replanner produces a revised step
///     list, a new plan-review row + checkpoint open for round N+1, and a
///     <c>plan_review_next_round</c> hub event fires against the owning
///     session group so the reviewer UI can surface the new request id
///     without leaking to any other connected client.</item>
///   <item>Terminal without execution → plan status transitions to
///     <c>Failed</c> with the terminal reason; the failure reply is persisted
///     and broadcast to the owning session group.</item>
/// </list>
///
/// <para>
/// <b>Broadcast scoping (#141).</b> Every SignalR event this service emits
/// carries subject-identifying content (<c>subject</c>, <c>reply</c>,
/// <c>charts</c>) and MUST be delivered only to the plan's owning session
/// group. Missing / whitespace session identity fails closed: the broadcast
/// is suppressed with a warning, never falling back to
/// <c>Clients.All</c>. The client-side <see cref="TelemetryHub.JoinSession"/>
/// binding — gated by <see cref="ISessionOwnershipRegistry"/> — is the sole
/// route by which a connection joins the group, so this delivery contract
/// composes with the existing hostile-rejoin protection (#92).
/// </para>
///
/// <para>
/// The completion service is idempotent: if the plan is already Completed or
/// Failed, ResolveAsync short-circuits. This lets the restart recovery
/// service race the endpoint without duplicating work.
/// </para>
/// </summary>
public sealed class PlanReviewCompletionService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PlanReviewCompletionService> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public PlanReviewCompletionService(
        IServiceScopeFactory scopeFactory,
        ILogger<PlanReviewCompletionService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Drive the plan through its next terminal or intermediate state after a
    /// reviewer decision has been recorded on the approval row. Returns a
    /// summary of the transition — Executed / SuspendedForNextRound /
    /// SuspendedForClarification / TerminatedWithoutExecution / NoOp.
    /// </summary>
    public async Task<PlanReviewCompletionResult> ResolveAsync(
        string planId, string subject, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;

        IPlanStore planStore = sp.GetRequiredService<IPlanStore>();
        PlanDetailDto? plan = await planStore.GetPlanAsync(subject, planId, ct);
        if (plan is null)
        {
            _logger.LogWarning("Resolve requested for missing plan {PlanId}/{Subject}.", planId, subject);
            return PlanReviewCompletionResult.NoOp("plan not found");
        }

        // Idempotency guard: once we've moved to a terminal status, ignore
        // subsequent resolve calls so the endpoint and restart recovery can
        // race safely.
        if (plan.Status is PlanStatus.Completed or PlanStatus.Failed
            or PlanStatus.Cancelled or PlanStatus.Unusable)
        {
            return PlanReviewCompletionResult.NoOp("plan already terminal");
        }

        PlanReviewCheckpointService checkpoints = sp.GetRequiredService<PlanReviewCheckpointService>();
        PlanReviewCheckpointState? state = await checkpoints.LoadLatestAsync(planId, ct);
        if (state is null)
        {
            _logger.LogError(
                "Plan {PlanId} has no framework checkpoint at resume time; terminating as failed.",
                planId);
            await FinaliseAsFailedAsync(planStore, plan, subject,
                $"{PlanReviewTerminalReason.ReviewTimedOut}: no checkpoint present at resume time.",
                sp, ct);
            return PlanReviewCompletionResult.TerminatedWithoutExecution(
                PlanReviewTerminalReason.ReviewTimedOut,
                "no checkpoint present at resume time");
        }

        return state.Kind switch
        {
            PlanCheckpointKind.Review =>
                await ResolveReviewAsync(sp, planStore, plan, state, ct),
            PlanCheckpointKind.Clarification =>
                await ResolveClarificationAsync(sp, planStore, plan, state, ct),
            _ => PlanReviewCompletionResult.NoOp($"unknown checkpoint kind '{state.Kind}'"),
        };
    }

    // ── Review branch ────────────────────────────────────────────────────

    private async Task<PlanReviewCompletionResult> ResolveReviewAsync(
        IServiceProvider sp,
        IPlanStore planStore,
        PlanDetailDto plan,
        PlanReviewCheckpointState state,
        CancellationToken ct)
    {
        IApprovalGate gate = sp.GetRequiredService<IApprovalGate>();
        PlanReviewCoordinator coord = sp.GetRequiredService<PlanReviewCoordinator>();

        ApprovalRequest? row = await FindLatestRowForPlanAsync(
            gate, plan.PlanId, state.Subject, ApprovalKind.PlanReview, ct);
        if (row is null)
        {
            return PlanReviewCompletionResult.NoOp("no plan-review row for plan yet");
        }
        if (row.Decision == ApprovalDecision.Pending)
        {
            // Row exists but has not decided yet — nothing to do; the sweep
            // or the endpoint will call us back after the decision lands.
            return PlanReviewCompletionResult.NoOp("review row still pending");
        }

        ApprovalResult result = new(
            row.RequestId, row.Decision, row.Comment, row.RespondedAt,
            row.TerminalReason, row.ResponsePayload);

        PlanReviewContinuation continuation = coord.EvaluateDecision(new PlanReviewEvaluationInput
        {
            PlanId = state.PlanId,
            RoundNumber = state.RoundNumber,
            CurrentSteps = state.Steps,
            SpecialistKeys = state.SpecialistKeys,
        }, result);

        switch (continuation.Kind)
        {
            case PlanReviewContinuationKind.Approved:
                {
                    // Atomic claim: two concurrent resume drivers (endpoint kickoff
                    // + restart recovery, or two endpoint callers racing the same
                    // approval row) both observe the persisted winner via
                    // FindLatestRowForPlanAsync, then both would call
                    // ExecuteApprovedPlanAsync and run the specialists twice.
                    // Collapse that race to one caller via a conditional
                    // AwaitingReview → Running transition; the loser NoOps.
                    bool claimed = await planStore.TryTransitionStatusAsync(
                        plan.PlanId, state.Subject,
                        PlanStatus.AwaitingReview, PlanStatus.Running, ct);
                    return !claimed
                        ? PlanReviewCompletionResult.NoOp(
                            "another resume driver already claimed this plan for execution")
                        : await ExecuteApprovedPlanAsync(
                        sp, planStore, plan, state,
                        continuation.ApprovedSteps ?? state.Steps,
                        continuation.TerminalReason, ct);
                }

            case PlanReviewContinuationKind.Terminal:
                {
                    // Same-race guard as the Approved branch: only the caller that
                    // wins the AwaitingReview → Failed transition finalizes the
                    // row and broadcasts. The loser NoOps so the SignalR surface
                    // sees exactly one terminal broadcast.
                    bool claimed = await planStore.TryTransitionStatusAsync(
                        plan.PlanId, state.Subject,
                        PlanStatus.AwaitingReview, PlanStatus.Failed, ct);
                    if (!claimed)
                    {
                        return PlanReviewCompletionResult.NoOp(
                            "another resume driver already terminated this plan");
                    }
                    await FinaliseAsFailedAsync(planStore, plan, state.Subject,
                        $"{continuation.TerminalReason}: {continuation.FailureMessage}", sp, ct);
                    await BroadcastFinalAsync(sp, plan.PlanId, state.Subject, state.SessionId,
                        BuildTerminalReply(continuation.TerminalReason), continuation.TerminalReason);
                    return PlanReviewCompletionResult.TerminatedWithoutExecution(
                        continuation.TerminalReason,
                        continuation.FailureMessage);
                }

            case PlanReviewContinuationKind.NeedsReplan:
                {
                    // Two callers with the same persisted-rejected decision would
                    // otherwise both write new checkpoints and both open new
                    // approval rows for round N+1. Claim exclusive ownership via
                    // AwaitingReview → Running; only the winner opens the new
                    // round. Restore to AwaitingReview after so external
                    // observers (poll, list) see a coherent status.
                    bool claimed = await planStore.TryTransitionStatusAsync(
                        plan.PlanId, state.Subject,
                        PlanStatus.AwaitingReview, PlanStatus.Running, ct);
                    if (!claimed)
                    {
                        return PlanReviewCompletionResult.NoOp(
                            "another resume driver already opened the next round");
                    }

                    // Rehydrate the roster from the current process's DI so we
                    // replan against the same specialists the executor will run.
                    IReadOnlyList<ISpecialistAgent> roster =
                        [.. sp.GetRequiredService<IEnumerable<ISpecialistAgent>>()];
                    PlanBuildResult? replanned = await coord.ReplanAsync(
                        state.Request, continuation.RejectionFeedback,
                        roster, state.DetectedIntents, ct);
                    if (replanned is null || replanned.IsUnusable)
                    {
                        string msg = replanned?.UnusableReason ?? "planner unavailable";
                        // We already own the claim, so a straight FinaliseAsFailed
                        // (unconditional Running → Failed) is safe: no other
                        // caller reached this branch.
                        await FinaliseAsFailedAsync(planStore, plan, state.Subject,
                            $"{PlanReviewTerminalReason.ReplanExhausted}: {msg}", sp, ct);
                        await BroadcastFinalAsync(sp, plan.PlanId, state.Subject, state.SessionId,
                            BuildTerminalReply(PlanReviewTerminalReason.ReplanExhausted),
                            PlanReviewTerminalReason.ReplanExhausted);
                        return PlanReviewCompletionResult.TerminatedWithoutExecution(
                            PlanReviewTerminalReason.ReplanExhausted, msg);
                    }

                    var nextSteps = replanned.Steps
                        .Select(s => new PlanReviewStepDto
                        {
                            SpecialistKey = s.SpecialistKey,
                            Intent = s.Intent,
                            Action = s.Action,
                        })
                        .ToList();

                    PlanReviewRoundHandle nextHandle = await coord.OpenRoundAsync(new PlanReviewOpenInput
                    {
                        PlanId = state.PlanId,
                        Subject = state.Subject,
                        SessionId = state.SessionId,
                        TenantId = state.TenantId,
                        Request = state.Request,
                        CurrentSteps = nextSteps,
                        SpecialistKeys = state.SpecialistKeys,
                        DetectedIntents = state.DetectedIntents,
                        RoundNumber = state.RoundNumber + 1,
                        RevisionReason = string.IsNullOrWhiteSpace(continuation.RejectionFeedback)
                            ? "Reviewer rejected the previous plan."
                            : "Reviewer feedback: " + continuation.RejectionFeedback,
                        TraceId = state.TraceId,
                        ParentSpanId = state.ParentSpanId,
                        PrincipalKey = state.PrincipalKey,
                    }, ct);

                    // Restore the plan to AwaitingReview so it accurately
                    // reflects "a new round is open for the reviewer" before
                    // the SignalR broadcast fires.
                    await planStore.UpdatePlanStatusAsync(new PlanStatusUpdate
                    {
                        PlanId = plan.PlanId,
                        Subject = state.Subject,
                        Status = PlanStatus.AwaitingReview,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    }, ct);

                    // Broadcast that a new review round is waiting — scoped to
                    // the persisted session group (#141) so a different subject's
                    // UI never receives this reviewer's next-round pointer.
                    // Fail closed (log + no broadcast) when the session id is
                    // missing so an isolation-critical event never fans out to
                    // Clients.All by accident.
                    IHubContext<TelemetryHub>? hub = sp.GetService<IHubContext<TelemetryHub>>();
                    await SendToOwningSessionAsync(
                        hub, state.SessionId, "plan_review_next_round", new
                        {
                            planId = state.PlanId,
                            requestId = nextHandle.RequestId,
                            round = nextHandle.RoundNumber,
                        }, ct);

                    return PlanReviewCompletionResult.SuspendedForNextRound(
                        nextHandle.RequestId, nextHandle.RoundNumber);
                }

            default:
                return PlanReviewCompletionResult.NoOp($"unknown continuation kind {continuation.Kind}");
        }
    }

    // ── Clarification branch ─────────────────────────────────────────────

    private async Task<PlanReviewCompletionResult> ResolveClarificationAsync(
        IServiceProvider sp,
        IPlanStore planStore,
        PlanDetailDto plan,
        PlanReviewCheckpointState state,
        CancellationToken ct)
    {
        IApprovalGate gate = sp.GetRequiredService<IApprovalGate>();
        ApprovalRequest? row = await FindLatestRowForPlanAsync(
            gate, plan.PlanId, state.Subject, ApprovalKind.Clarification, ct);
        if (row is null)
            return PlanReviewCompletionResult.NoOp("no clarification row for plan yet");
        if (row.Decision == ApprovalDecision.Pending)
            return PlanReviewCompletionResult.NoOp("clarification row still pending");

        ApprovalResult result = new(
            row.RequestId, row.Decision, row.Comment, row.RespondedAt,
            row.TerminalReason, row.ResponsePayload);
        PlanClarificationResult clarification = PlanClarifier.InterpretAnswer(result);

        if (!clarification.IsAnswered)
        {
            // Concurrent-caller guard: only the caller that wins the
            // AwaitingClarification → Failed transition finalizes and
            // broadcasts; the loser NoOps.
            bool claimedTerminal = await planStore.TryTransitionStatusAsync(
                plan.PlanId, state.Subject,
                PlanStatus.AwaitingClarification, PlanStatus.Failed, ct);
            if (!claimedTerminal)
            {
                return PlanReviewCompletionResult.NoOp(
                    "another resume driver already terminated this clarification");
            }
            await FinaliseAsFailedAsync(planStore, plan, state.Subject,
                $"{clarification.TerminalReason}: reviewer did not provide a usable answer.",
                sp, ct);
            await BroadcastFinalAsync(sp, plan.PlanId, state.Subject, state.SessionId,
                BuildTerminalReply(clarification.TerminalReason), clarification.TerminalReason);
            return PlanReviewCompletionResult.TerminatedWithoutExecution(
                clarification.TerminalReason, "no usable clarification answer");
        }

        // Concurrent-caller guard for the answered path — only the caller
        // that flips AwaitingClarification → Running proceeds; the losing
        // caller NoOps. Without this the executor would run twice and the
        // final broadcast would fire twice per specialist.
        bool claimedRun = await planStore.TryTransitionStatusAsync(
            plan.PlanId, state.Subject,
            PlanStatus.AwaitingClarification, PlanStatus.Running, ct);
        if (!claimedRun)
        {
            return PlanReviewCompletionResult.NoOp(
                "another resume driver already claimed this clarification for execution");
        }

        // Substitute the answer as the paused step's Result, then execute the
        // remaining steps AFTER the paused step.
        //
        // Coherent checkpoint coordinate model (see PlanReviewCheckpointState
        // XML doc, #141): every suspension writer slices <c>Steps</c> so the
        // paused step sits at position 0 and every downstream step follows at
        // positions 1..N. The reader ALWAYS resolves the paused step as
        // <c>Steps[0]</c> and the downstream plan as <c>Steps.Skip(1)</c>.
        // Legacy checkpoints from before this fix wrote the same sliced Steps
        // but recorded <c>PausedAtStepIndex</c> as the ABSOLUTE index in the
        // ORIGINAL plan; the buggy reader treated that absolute value as an
        // index into the sliced list, which for any non-first clarification
        // either fabricated a synthetic paused step or produced an empty
        // remaining slice and silently skipped every downstream step. Under
        // the new model, <c>PausedAtStepIndex</c> is preserved for audit only
        // — the reader ignores it for element resolution so the same code
        // path works for both the buggy legacy shape and freshly written
        // checkpoints. The persisted <see cref="PlanReviewCompletedStep.StepIndex"/>
        // still uses the original absolute index so downstream numbering is
        // unchanged.

        PlanReviewStepDto paused = state.Steps.Count > 0
            ? state.Steps[0]
            : new PlanReviewStepDto
            {
                SpecialistKey = "clarification",
                Intent = "clarification",
                Action = "clarification",
            };
        // Cumulative context: every previously-completed step (from earlier
        // rounds — could include the transcripts of a prior clarification
        // resume) plus the just-answered paused step. Repeated clarifications
        // must not reset this list; the executor is seeded with the whole
        // transcript so a downstream [[CLARIFY]] on the resumed plan writes a
        // checkpoint whose CompletedSteps preserves every prior chart/result.
        List<PlanReviewCompletedStep> priorCompleted =
            [.. state.CompletedSteps ?? []];
        int absolutePausedIndex = state.PausedAtStepIndex
            ?? priorCompleted.Count;
        priorCompleted.Add(new PlanReviewCompletedStep
        {
            StepIndex = absolutePausedIndex,
            SpecialistKey = paused.SpecialistKey,
            Intent = paused.Intent,
            Action = paused.Action,
            Result = clarification.Answer ?? string.Empty,
            InputTokens = 0,
            OutputTokens = 0,
            TotalTokens = 0,
            DurationMs = 0,
        });

        IReadOnlyList<PlanReviewStepDto> remaining =
            [.. state.Steps.Skip(1)];
        return await ExecuteApprovedPlanAsync(
            sp, planStore, plan, state,
            remaining,
            PlanReviewTerminalReason.ReviewerApproved,
            ct,
            resumeCompletedSteps: priorCompleted,
            claimedRunning: true);
    }

    // ── Shared execute path ──────────────────────────────────────────────

    private async Task<PlanReviewCompletionResult> ExecuteApprovedPlanAsync(
        IServiceProvider sp,
        IPlanStore planStore,
        PlanDetailDto plan,
        PlanReviewCheckpointState state,
        IReadOnlyList<PlanReviewStepDto> effectiveSteps,
        string terminalReason,
        CancellationToken ct,
        IReadOnlyList<PlanReviewCompletedStep>? resumeCompletedSteps = null,
        bool claimedRunning = false)
    {
        // Callers on the Review-Approved and Terminal branches claim the
        // AwaitingReview → Running transition before calling us; the
        // clarification path passes `claimedRunning: true` so it does not
        // repeat the claim. When neither has claimed (initial plan-review
        // resume with no prior guard — currently unreachable but kept
        // defensive), we still write the plan into Running below so a
        // second concurrent caller observes it and short-circuits at the
        // top-level idempotency guard on the next round.
        _ = claimedRunning;

        // Move plan to Running and materialise step ids so the executor and
        // store agree on the same shape.
        var effectivePlan = new PlanBuildResult
        {
            Steps = [.. effectiveSteps.Select(s => new PlannerStep
            {
                SpecialistKey = s.SpecialistKey,
                Intent = s.Intent,
                Action = s.Action,
            })],
        };

        var stepIds = new List<string>(effectivePlan.Steps.Count);
        var stepWrites = new List<PlanStepWrite>(effectivePlan.Steps.Count);
        int offset = resumeCompletedSteps?.Count ?? 0;
        for (int i = 0; i < effectivePlan.Steps.Count; i++)
        {
            string stepId = $"{plan.PlanId}-r{state.RoundNumber}-s{offset + i}";
            stepIds.Add(stepId);
            stepWrites.Add(new PlanStepWrite
            {
                StepId = stepId,
                StepIndex = offset + i,
                SpecialistKey = effectivePlan.Steps[i].SpecialistKey,
                Intent = effectivePlan.Steps[i].Intent,
                Action = effectivePlan.Steps[i].Action,
                Status = PlanStepStatus.Pending,
            });
        }

        // Persist step rows atomically BEFORE the executor runs (#144
        // follow-up): the executor's per-step UPDATE (see
        // <see cref="IPlanStore.UpdateStepAsync"/>) only touches existing
        // rows keyed by <c>StepId</c>. Prior to this write the plan row
        // carried at most the ORIGINAL <c>{planId}-s{n}</c> ids from
        // <see cref="PlanOrchestrator.SuspendForReviewAsync"/>, so every
        // round-scoped <c>{planId}-r{r}-s{n}</c> step transition silently
        // no-op'd and the plan rehydrated as a zero-step ghost. The
        // conditional-plus-transactional replacement in
        // <see cref="IPlanStore.ReplacePlanStepsFromIndexAsync"/> preserves
        // rows for prior clarification-completed steps (index &lt; offset)
        // while replacing the round tail atomically so a concurrent reader
        // never sees a torn view. If the caller already claimed the plan
        // row to Running (Approved / NeedsReplan / answered-clarification
        // branches), the ownership-guarded UPDATE below is a no-op status
        // rewrite; when it did not claim, we still normalise the row here
        // so the executor's own status writes land against a coherent
        // baseline.
        await planStore.ReplacePlanStepsFromIndexAsync(
            plan.PlanId, state.Subject, offset, stepWrites, ct);

        // Persist the resolved (edited/approved) plan status onto the same row.
        await planStore.UpdatePlanStatusAsync(new PlanStatusUpdate
        {
            PlanId = plan.PlanId,
            Subject = state.Subject,
            Status = PlanStatus.Running,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, ct);

        // Roster + lookup from the CURRENT process's DI so the resume is
        // self-contained (no cross-process handle references).
        IReadOnlyList<ISpecialistAgent> roster =
            [.. sp.GetRequiredService<IEnumerable<ISpecialistAgent>>()];
        var lookup = roster.ToDictionary(s => s.Key, s => s, StringComparer.OrdinalIgnoreCase);

        var executionRequest = new PlanExecutionRequest
        {
            PlanId = plan.PlanId,
            Subject = state.Subject,
            PrincipalKey = state.PrincipalKey ?? state.Subject,
            SessionId = state.SessionId,
            TraceId = state.TraceId ?? Guid.NewGuid().ToString("N"),
            ParentSpanId = state.ParentSpanId,
            Request = state.Request,
            History = null,
            User = null,
            Plan = effectivePlan,
            StepIds = stepIds,
            SpecialistLookup = lookup,
            // Seed the executor's initial AccumulatedResults with every
            // previously-completed step (from earlier rounds and prior
            // clarification answers). A downstream [[CLARIFY]] emitted by
            // the resumed plan's executor sees the FULL transcript through
            // its PlanStepMessage.AccumulatedResults, so the checkpoint it
            // opens carries every prior chart/result forward — otherwise
            // repeated clarifications would silently drop all pre-resume
            // state.
            PriorAccumulatedResults = resumeCompletedSteps is { Count: > 0 }
                ? [.. resumeCompletedSteps.Select(ToStepResult)]
                : [],
        };

        PlanExecutor executor = sp.GetRequiredService<PlanExecutor>();
        PlanExecutionOutcome outcome;
        try
        {
            outcome = await executor.ExecuteAsync(executionRequest, ct);
        }
        catch (Exception ex) when (ex is not null)
        {
            // Recoverable execution claim (#144 follow-up): once we've
            // atomically claimed the plan row to Running, a subsequent
            // exception or cancellation from the executor MUST land the
            // plan in an honest terminal state — otherwise the plan is
            // stranded in Running forever, the reviewer sees an in-flight
            // spinner that never resolves, and restart recovery (which
            // currently skips Running rows) never picks it up. We
            // transition Running → Failed with a stable failure reason so
            // the durable row matches the terminal approval, then re-throw
            // so the caller (KickoffCompletion / restart recovery driver)
            // can log at the appropriate site. Any Failed rewrite here is
            // best-effort and never propagates its own exception.
            string failureReason = ex is OperationCanceledException
                ? "plan-resume cancelled after execution claim"
                : $"plan-resume threw during execution: {ex.GetType().Name}";
            try
            {
                await planStore.TryTransitionStatusAsync(
                    plan.PlanId, state.Subject,
                    PlanStatus.Running, PlanStatus.Failed, CancellationToken.None);
                await planStore.UpdatePlanStatusAsync(new PlanStatusUpdate
                {
                    PlanId = plan.PlanId,
                    Subject = state.Subject,
                    Status = PlanStatus.Failed,
                    FailureReason = failureReason,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }, CancellationToken.None);
            }
            catch (Exception recoveryEx)
            {
                _logger.LogError(recoveryEx,
                    "Plan {PlanId} recoverable-Failed transition itself failed after execution exception; row remains as-is.",
                    plan.PlanId);
            }
            _logger.LogError(ex,
                "Plan {PlanId} resume execution failed after claim; marked Failed with reason '{Reason}'.",
                plan.PlanId, failureReason);
            throw;
        }

        // If the executor asks to pause for clarification / replan, hand off.
        if (outcome.Status == PlanStatus.AwaitingClarification
            && outcome.ClarificationHandle is { } clarHandle)
        {
            // Subsequent-clarification user visibility (#144 follow-up): the
            // resumed plan paused for another clarification, but the
            // frontend's `usePlanController` only opens the
            // `PlanClarificationCard` when it receives an
            // `approval_requested` event scoped to this session. The initial
            // approval broadcast happens from the ApprovalTool; a mid-resume
            // clarification opens the row inside the executor with no hub
            // notification of its own. We fire the same event shape the
            // frontend already understands, scoped to the persisted session
            // group (fail-closed on missing session id), only after the
            // executor's finally block has committed AwaitingClarification
            // — so the reviewer never sees an approval before its
            // awaiting-* status is durable.
            await SendClarificationOpenedAsync(
                sp, plan.PlanId, state.Subject, state.SessionId, clarHandle);
            return PlanReviewCompletionResult.SuspendedForClarification(
                clarHandle.RequestId, clarHandle.CheckpointId);
        }

        if (outcome.Status == PlanStatus.AwaitingReview
            && outcome.ReviewHandle is { } reviewHandle)
        {
            return PlanReviewCompletionResult.SuspendedForNextRound(
                reviewHandle.RequestId, reviewHandle.RoundNumber);
        }

        // Compose the final reply from step transcripts (approved-review path
        // + any resumed clarification prefixes).
        string composed = ComposeFinalReply(resumeCompletedSteps, outcome);

        // Output guardrail + PII filter — parity with /api/chat single-
        // specialist path.
        GuardrailsMiddleware guardrails = sp.GetRequiredService<GuardrailsMiddleware>();
        string filtered = await guardrails.FilterOutputAsync(composed, state.Subject, ct);

        // Chart propagation on the resume path (#137): specialists on an
        // approved / edited / clarification-resumed plan emit ChartSpecs
        // exactly the same way they do on the fast path and on the
        // immediate plan-first branch. Prior to this change, the resume
        // path composed the final reply text but silently dropped every
        // chart the executor produced — the SignalR broadcast delivered
        // <c>reply</c> only, so ADR-006's "9 chart types render on both
        // paths" invariant broke as soon as a plan needed reviewer
        // approval. We flatten Charts in specialist order (mirroring
        // <see cref="PlanOrchestrationResult.Charts"/>) so a plan resumed
        // by the reviewer surface delivers the same chart list a plan
        // that never needed review would.
        //
        // On the clarification-resume path, <paramref name="resumeCompletedSteps"/>
        // carries the pre-suspend transcript. Those steps' charts must survive
        // the pause too, so they're flattened ahead of the post-resume outcome
        // steps to preserve specialist order.
        IReadOnlyList<ChartSpec> planCharts =
        [
            .. (resumeCompletedSteps ?? []).SelectMany(s => s.Charts ?? []),
            .. outcome.Steps.SelectMany(s => s.Charts ?? []),
        ];

        await FinaliseAsCompletedAsync(planStore, plan.PlanId, state.Subject,
            filtered, terminalReason, outcome, ct);

        // Chat-turn parity — mirror the trio the single-specialist branch fires.
        await ApplyChatTurnParityAsync(sp, plan, state, filtered, outcome, ct);

        await BroadcastFinalAsync(sp, plan.PlanId, state.Subject, state.SessionId,
            filtered, terminalReason, planCharts);

        return PlanReviewCompletionResult.Executed(filtered, outcome, planCharts);
    }

    /// <summary>
    /// Convert a persisted <see cref="PlanReviewCompletedStep"/> into the
    /// executor's per-step transcript shape so the initial
    /// <see cref="PlanStepMessage.AccumulatedResults"/> reflects the full
    /// pre-resume history — including specialist charts.
    /// </summary>
    private static PlanStepResult ToStepResult(PlanReviewCompletedStep s) => new(
        StepIndex: s.StepIndex,
        StepId: $"resumed-{s.StepIndex}",
        SpecialistKey: s.SpecialistKey,
        Intent: s.Intent,
        Action: s.Action,
        Status: PlanStepStatus.Completed,
        Result: s.Result,
        Error: null,
        InputTokens: s.InputTokens,
        OutputTokens: s.OutputTokens,
        TotalTokens: s.TotalTokens,
        DurationMs: s.DurationMs)
    {
        Charts = s.Charts is { Count: > 0 } charts ? [.. charts] : null,
    };

    private static string ComposeFinalReply(
        IReadOnlyList<PlanReviewCompletedStep>? resumeCompletedSteps,
        PlanExecutionOutcome outcome)
    {
        var sb = new StringBuilder();
        if (resumeCompletedSteps is not null)
        {
            foreach (PlanReviewCompletedStep step in resumeCompletedSteps)
            {
                if (string.IsNullOrWhiteSpace(step.Result)) continue;
                if (sb.Length > 0) sb.AppendLine().AppendLine("---").AppendLine();
                sb.Append(step.Result);
            }
        }
        foreach (PlanStepResult step in outcome.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Result)) continue;
            if (sb.Length > 0) sb.AppendLine().AppendLine("---").AppendLine();
            sb.Append(step.Result);
        }
        if (sb.Length == 0)
        {
            sb.Append(outcome.Status switch
            {
                PlanStatus.Failed => "The plan-first orchestrator was unable to produce a reply. " +
                    (outcome.FailureReason ?? "One or more steps failed."),
                PlanStatus.Cancelled => "The plan-first orchestrator was cancelled before producing a reply.",
                _ => "The plan-first orchestrator produced no output.",
            });
        }
        return sb.ToString();
    }

    private static string BuildTerminalReply(string terminalReason) => terminalReason switch
    {
        PlanReviewTerminalReason.ReviewTimedOut =>
            "The plan was not executed because reviewer approval timed out.",
        PlanReviewTerminalReason.ReplanExhausted =>
            "The plan was not executed because the reviewer rejected every revision within the configured limit.",
        PlanReviewTerminalReason.EditedToEmpty =>
            "The plan was not executed because the reviewer edited it down to zero steps.",
        PlanReviewTerminalReason.EditInvalid =>
            "The plan was not executed because the reviewer's edited step list referenced an unknown specialist.",
        PlanReviewTerminalReason.ClarificationInvalid =>
            "The plan was not executed because the mid-plan clarification response was not usable.",
        _ => "The plan was not executed because the reviewer declined to approve it.",
    };

    private static async Task<ApprovalRequest?> FindLatestRowForPlanAsync(
        IApprovalGate gate, string planId, string subject, string kind, CancellationToken ct)
    {
        IReadOnlyList<ApprovalRequest> pending = await gate.GetPendingAsync(subject, ct);
        ApprovalRequest? bestPending = pending
            .Where(r => string.Equals(r.Context.PlanId, planId, StringComparison.Ordinal)
                && string.Equals(r.Context.Kind, kind, StringComparison.Ordinal))
            .OrderByDescending(r => r.Context.RoundNumber)
            .ThenByDescending(r => r.CreatedAt)
            .FirstOrDefault();
        if (bestPending is not null) return bestPending;

        IReadOnlyList<ApprovalRequest> history = await gate.GetHistoryAsync(500, ct);
        return history
            .Where(r => string.Equals(r.Context.PlanId, planId, StringComparison.Ordinal)
                && string.Equals(r.Context.UserId, subject, StringComparison.Ordinal)
                && string.Equals(r.Context.Kind, kind, StringComparison.Ordinal))
            .OrderByDescending(r => r.Context.RoundNumber)
            .ThenByDescending(r => r.RespondedAt ?? r.CreatedAt)
            .FirstOrDefault();
    }

    private static async Task FinaliseAsFailedAsync(
        IPlanStore planStore,
        PlanDetailDto plan,
        string subject,
        string failureReason,
        IServiceProvider sp,
        CancellationToken ct)
    {
        await planStore.UpdatePlanStatusAsync(new PlanStatusUpdate
        {
            PlanId = plan.PlanId,
            Subject = subject,
            Status = PlanStatus.Failed,
            FailureReason = failureReason,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, ct);
    }

    private static async Task FinaliseAsCompletedAsync(
        IPlanStore planStore,
        string planId,
        string subject,
        string finalReply,
        string terminalReason,
        PlanExecutionOutcome outcome,
        CancellationToken ct)
    {
        // Preserve the reply on the plan record so a later GET /api/plans/{id}
        // returns the same text the reviewer/subject saw on the SignalR
        // broadcast. The FailureReason column doubles as the terminal transcript
        // marker keyed by <c>PlanReviewFinalReply</c> prefix so downstream
        // consumers don't need a schema change.
        string payload = "PlanReviewFinalReply::" + terminalReason + "::" + finalReply;
        await planStore.UpdatePlanStatusAsync(new PlanStatusUpdate
        {
            PlanId = planId,
            Subject = subject,
            Status = outcome.Status is PlanStatus.Completed
                or PlanStatus.Failed or PlanStatus.Cancelled
                    ? outcome.Status : PlanStatus.Completed,
            FailureReason = payload,
            TotalInputTokens = outcome.Steps.Sum(s => s.InputTokens),
            TotalOutputTokens = outcome.Steps.Sum(s => s.OutputTokens),
            TotalTokens = outcome.Steps.Sum(s => s.TotalTokens),
            TotalDurationMs = outcome.DurationMs,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, ct);
    }

    private async Task ApplyChatTurnParityAsync(
        IServiceProvider sp,
        PlanDetailDto plan,
        PlanReviewCheckpointState state,
        string filteredReply,
        PlanExecutionOutcome outcome,
        CancellationToken ct)
    {
        try
        {
            IAuditLog auditLog = sp.GetRequiredService<IAuditLog>();
            ConversationExporter exporter = sp.GetRequiredService<ConversationExporter>();
            ISessionStore? sessionStore = sp.GetService<ISessionStore>();
            IOptions<SessionPersistenceOptions>? sessionOpts =
                sp.GetService<IOptions<SessionPersistenceOptions>>();
            ITenantProvider tenantProvider = sp.GetRequiredService<ITenantProvider>();

            int totalTokens = outcome.Steps.Sum(s => s.TotalTokens);
            var duration = TimeSpan.FromMilliseconds(outcome.DurationMs);

            await auditLog.LogAsync(new AuditEntry(
                Guid.NewGuid().ToString("N"),
                DateTime.UtcNow, state.Subject, "planner",
                $"chat.plan.review.resolve",
                state.Request[..Math.Min(200, state.Request.Length)],
                filteredReply[..Math.Min(200, filteredReply.Length)],
                totalTokens, duration), ct);

            string? sessionId = state.SessionId;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                await exporter.TrackMessageAsync(sessionId, new TrackedMessage
                {
                    Role = "user",
                    Content = state.Request,
                }, ct);
                await exporter.TrackMessageAsync(sessionId, new TrackedMessage
                {
                    Role = "assistant",
                    Content = filteredReply,
                    AgentId = "planner",
                    DurationMs = outcome.DurationMs,
                    Tokens = totalTokens,
                }, ct);

                bool persistenceEnabled = sessionStore is not null
                    && sessionOpts is not null
                    && sessionOpts.Value.Enabled;
                if (persistenceEnabled)
                {
                    string? tenantId = tenantProvider.GetTenant()?.Company;
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    await sessionStore!.PersistTurnAsync(new SessionTurnWrite
                    {
                        SessionId = sessionId,
                        Subject = state.Subject,
                        TenantId = tenantId,
                        Role = "user",
                        Content = state.Request,
                        RoutingIntent = "plan",
                        RoutingAgentKey = "planner",
                        RoutingConfidence = 0,
                        Timestamp = now,
                    }, ct);
                    await sessionStore.PersistTurnAsync(new SessionTurnWrite
                    {
                        SessionId = sessionId,
                        Subject = state.Subject,
                        TenantId = tenantId,
                        Role = "assistant",
                        Content = filteredReply,
                        AgentId = "planner",
                        RoutingIntent = "plan",
                        RoutingAgentKey = "planner",
                        RoutingConfidence = 0,
                        InputTokens = outcome.Steps.Sum(s => s.InputTokens),
                        OutputTokens = outcome.Steps.Sum(s => s.OutputTokens),
                        TotalTokens = totalTokens,
                        SpanSummary = JsonSerializer.Serialize(new
                        {
                            agent = "planner",
                            planId = plan.PlanId,
                            status = outcome.Status,
                            steps = outcome.Steps.Count,
                            durationMs = outcome.DurationMs,
                        }),
                        Timestamp = now,
                    }, ct);
                }
            }
        }
        catch (Exception ex)
        {
            // Chat-turn parity is best-effort by design (mirrors ChatEndpoints).
            _logger.LogWarning(ex,
                "Chat-turn parity write failed for plan {PlanId}; final response was still delivered.",
                plan.PlanId);
        }
    }

    private async Task BroadcastFinalAsync(
        IServiceProvider sp, string planId, string subject, string? sessionId,
        string reply, string terminalReason,
        IReadOnlyList<ChartSpec>? charts = null)
    {
        IHubContext<TelemetryHub>? hub = sp.GetService<IHubContext<TelemetryHub>>();
        // Session-scoped delivery (#141): the plan_final_response payload
        // carries subject-identifying content — subject id, the full reply
        // text, and any specialist chart data — so it MUST be delivered to
        // the owning session's group only. See <see cref="SendToOwningSessionAsync"/>
        // for the fail-closed semantics on missing session identity.
        //
        // Charts flattened in specialist order — matches the
        // ChatResponse.Charts contract on the fast path so the client
        // renders identical charts regardless of whether the plan was
        // executed immediately or resumed via the review surface. Null
        // or empty means the specialists produced no charts on this
        // turn. Non-terminal (early replan / clarification) broadcasts
        // don't reach this method — those go via plan_review_next_round
        // instead.
        await SendToOwningSessionAsync(
            hub, sessionId, "plan_final_response", new
            {
                planId,
                subject,
                reply,
                terminalReason,
                charts = charts is { Count: > 0 } ? charts : null,
            });
    }

    /// <summary>
    /// Fire the existing <c>approval_requested</c> hub event scoped to the
    /// owning session group so <c>usePlanController</c> can render
    /// <c>PlanClarificationCard</c> for a subsequent clarification that opens
    /// on a resumed plan. The plan status is already durable at this call
    /// site — the executor's finally block committed
    /// <see cref="PlanStatus.AwaitingClarification"/> before returning — so
    /// a listener that resolves the clarification immediately still finds a
    /// coherent awaiting-* row. Session-scoped: fails closed on missing
    /// session identity so a plan that lost its session id never fans out to
    /// <see cref="IHubClients.All"/>.
    /// </summary>
    private async Task SendClarificationOpenedAsync(
        IServiceProvider sp,
        string planId,
        string subject,
        string? sessionId,
        PlanClarificationHandle handle)
    {
        IHubContext<TelemetryHub>? hub = sp.GetService<IHubContext<TelemetryHub>>();

        // Look up the just-opened clarification row so the broadcast carries
        // the same PlanClarificationPrompt payload the initial ApprovalTool
        // event already delivers. `usePlanController.parseClarificationPrompt`
        // decodes this JSON to render the prompt in `PlanClarificationCard`.
        // Falling back to a minimal payload (planId only) still lets the
        // frontend open the card and fetch the row detail out-of-band, so a
        // gate lookup failure never blocks user visibility.
        string? payload = null;
        try
        {
            IApprovalGate? gate = sp.GetService<IApprovalGate>();
            if (gate is not null)
            {
                IReadOnlyList<ApprovalRequest> pending = await gate.GetPendingAsync(subject, CancellationToken.None);
                ApprovalRequest? row = pending.FirstOrDefault(r =>
                    string.Equals(r.RequestId, handle.RequestId, StringComparison.Ordinal));
                payload = row?.Context.Payload;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "approval_requested clarification broadcast could not read payload for plan {PlanId} request {RequestId}; frontend will render a minimal prompt.",
                planId, handle.RequestId);
        }

        // Mirror the shape usePlanController.ts already parses from
        // ApprovalTool: `context.planId`, `context.kind`, `context.payload`
        // + top-level `id`. The frontend dispatches
        // CLARIFICATION_REQUESTED off `context.kind === 'clarification'`.
        await SendToOwningSessionAsync(
            hub, sessionId, "approval_requested", new
            {
                id = handle.RequestId,
                planId,
                kind = "clarification",
                context = new
                {
                    planId,
                    userId = subject,
                    kind = "clarification",
                    roundNumber = 0,
                    payload,
                },
            });
    }

    /// <summary>
    /// Session-scoped SignalR delivery for plan-path events (#141). Sends the
    /// payload to <c>Clients.Group(sessionId)</c> — the same group the
    /// <see cref="TelemetryHub.JoinSession"/> ownership gate populates — so
    /// only the plan's owning subject receives it. A null/whitespace
    /// <paramref name="sessionId"/> fails closed: the send is suppressed with
    /// a warning and the caller MUST NOT fall back to <c>Clients.All</c> or
    /// any other broadcast surface. That matches the existing session-scoped
    /// telemetry model (see <c>ISessionOwnershipRegistry</c>, issue #92) and
    /// contains any regression that lets a plan record land without a
    /// session id.
    /// </summary>
    private async Task SendToOwningSessionAsync(
        IHubContext<TelemetryHub>? hub,
        string? sessionId,
        string method,
        object payload,
        CancellationToken ct = default)
    {
        if (hub is null)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            _logger.LogWarning(
                "Plan-path SignalR event {Method} suppressed: session identity is missing on the resolved plan; refusing to broadcast to Clients.All to avoid cross-session leak.",
                method);
            return;
        }
        await hub.Clients.Group(sessionId).SendAsync(method, payload, ct);
    }
}

/// <summary>Outcome of one call to <see cref="PlanReviewCompletionService.ResolveAsync"/>.</summary>
public sealed record PlanReviewCompletionResult
{
    public required PlanReviewCompletionKind Kind { get; init; }
    public string? Reply { get; init; }
    public string? TerminalReason { get; init; }
    public string? FailureMessage { get; init; }
    public string? NextRequestId { get; init; }
    public int? NextRoundNumber { get; init; }
    public string? ClarificationRequestId { get; init; }
    public string? ClarificationCheckpointId { get; init; }
    public PlanExecutionOutcome? ExecutionOutcome { get; init; }

    /// <summary>
    /// Charts collected from every executed step on the resume path,
    /// flattened in specialist order. Only populated on the
    /// <see cref="PlanReviewCompletionKind.Executed"/> branch. Empty when
    /// no specialist emitted a chart; never <see langword="null"/> so
    /// callers can iterate unconditionally.
    /// </summary>
    public IReadOnlyList<ChartSpec> Charts { get; init; } = [];

    public static PlanReviewCompletionResult Executed(
        string reply,
        PlanExecutionOutcome outcome,
        IReadOnlyList<ChartSpec>? charts = null) => new()
        {
            Kind = PlanReviewCompletionKind.Executed,
            Reply = reply,
            ExecutionOutcome = outcome,
            Charts = charts ?? [],
        };

    public static PlanReviewCompletionResult SuspendedForNextRound(string requestId, int round) => new()
    {
        Kind = PlanReviewCompletionKind.SuspendedForNextRound,
        NextRequestId = requestId,
        NextRoundNumber = round,
    };

    public static PlanReviewCompletionResult SuspendedForClarification(string requestId, string checkpointId) => new()
    {
        Kind = PlanReviewCompletionKind.SuspendedForClarification,
        ClarificationRequestId = requestId,
        ClarificationCheckpointId = checkpointId,
    };

    public static PlanReviewCompletionResult TerminatedWithoutExecution(string reason, string? failure) => new()
    {
        Kind = PlanReviewCompletionKind.TerminatedWithoutExecution,
        TerminalReason = reason,
        FailureMessage = failure,
    };

    public static PlanReviewCompletionResult NoOp(string reason) => new()
    {
        Kind = PlanReviewCompletionKind.NoOp,
        FailureMessage = reason,
    };
}

public enum PlanReviewCompletionKind
{
    Executed,
    SuspendedForNextRound,
    SuspendedForClarification,
    TerminatedWithoutExecution,
    NoOp,
}

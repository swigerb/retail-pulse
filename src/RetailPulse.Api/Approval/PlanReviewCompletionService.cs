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
///     broadcast over the SignalR hub as <c>plan_final_response</c>. Plan
///     status transitions to <c>Completed</c> / <c>Failed</c>.</item>
///   <item>Reject with cap remaining → the replanner produces a revised step
///     list, a new plan-review row + checkpoint open for round N+1, and a
///     <c>plan_review_next_round</c> hub event fires so the reviewer UI can
///     surface the new request id.</item>
///   <item>Terminal without execution → plan status transitions to
///     <c>Failed</c> with the terminal reason; the failure reply is persisted
///     and broadcast.</item>
/// </list>
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
                return await ExecuteApprovedPlanAsync(
                    sp, planStore, plan, state,
                    continuation.ApprovedSteps ?? state.Steps,
                    continuation.TerminalReason, ct);

            case PlanReviewContinuationKind.Terminal:
                await FinaliseAsFailedAsync(planStore, plan, state.Subject,
                    $"{continuation.TerminalReason}: {continuation.FailureMessage}", sp, ct);
                await BroadcastFinalAsync(sp, plan.PlanId, state.Subject,
                    BuildTerminalReply(continuation.TerminalReason), continuation.TerminalReason);
                return PlanReviewCompletionResult.TerminatedWithoutExecution(
                    continuation.TerminalReason,
                    continuation.FailureMessage);

            case PlanReviewContinuationKind.NeedsReplan:
                {
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
                        await FinaliseAsFailedAsync(planStore, plan, state.Subject,
                            $"{PlanReviewTerminalReason.ReplanExhausted}: {msg}", sp, ct);
                        await BroadcastFinalAsync(sp, plan.PlanId, state.Subject,
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

                    // Broadcast that a new review round is waiting.
                    IHubContext<TelemetryHub>? hub = sp.GetService<IHubContext<TelemetryHub>>();
                    if (hub is not null)
                    {
                        await hub.Clients.All.SendAsync("plan_review_next_round", new
                        {
                            planId = state.PlanId,
                            requestId = nextHandle.RequestId,
                            round = nextHandle.RoundNumber,
                        }, ct);
                    }

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
            await FinaliseAsFailedAsync(planStore, plan, state.Subject,
                $"{clarification.TerminalReason}: reviewer did not provide a usable answer.",
                sp, ct);
            await BroadcastFinalAsync(sp, plan.PlanId, state.Subject,
                BuildTerminalReply(clarification.TerminalReason), clarification.TerminalReason);
            return PlanReviewCompletionResult.TerminatedWithoutExecution(
                clarification.TerminalReason, "no usable clarification answer");
        }

        // Substitute the answer as the paused step's Result, then execute the
        // remaining steps starting from PausedAtStepIndex + 1.
        int pauseIndex = state.PausedAtStepIndex ?? 0;
        List<PlanReviewCompletedStep> priorCompleted =
            [.. state.CompletedSteps ?? []];

        // The paused step itself becomes an "answer" step with the answer as
        // its transcript so the accumulated context reads coherently.
        PlanReviewStepDto paused = pauseIndex < state.Steps.Count
            ? state.Steps[pauseIndex]
            : new PlanReviewStepDto
            {
                SpecialistKey = "clarification",
                Intent = "clarification",
                Action = "clarification",
            };
        priorCompleted.Add(new PlanReviewCompletedStep
        {
            StepIndex = pauseIndex,
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
            [.. state.Steps.Skip(pauseIndex + 1)];
        return await ExecuteApprovedPlanAsync(
            sp, planStore, plan, state,
            remaining,
            PlanReviewTerminalReason.ReviewerApproved,
            ct,
            resumeCompletedSteps: priorCompleted);
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
        IReadOnlyList<PlanReviewCompletedStep>? resumeCompletedSteps = null)
    {
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

        // Persist the resolved (edited/approved) plan onto the same row.
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
        };

        PlanExecutor executor = sp.GetRequiredService<PlanExecutor>();
        PlanExecutionOutcome outcome = await executor.ExecuteAsync(executionRequest, ct);

        // If the executor asks to pause for clarification / replan, hand off.
        if (outcome.Status == PlanStatus.AwaitingClarification
            && outcome.ClarificationHandle is { } clarHandle)
        {
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

        await FinaliseAsCompletedAsync(planStore, plan.PlanId, state.Subject,
            filtered, terminalReason, outcome, ct);

        // Chat-turn parity — mirror the trio the single-specialist branch fires.
        await ApplyChatTurnParityAsync(sp, plan, state, filtered, outcome, ct);

        await BroadcastFinalAsync(sp, plan.PlanId, state.Subject, filtered, terminalReason);

        return PlanReviewCompletionResult.Executed(filtered, outcome);
    }

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
            TimeSpan duration = TimeSpan.FromMilliseconds(outcome.DurationMs);

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

    private static async Task BroadcastFinalAsync(
        IServiceProvider sp, string planId, string subject, string reply, string terminalReason)
    {
        IHubContext<TelemetryHub>? hub = sp.GetService<IHubContext<TelemetryHub>>();
        if (hub is null) return;
        await hub.Clients.All.SendAsync("plan_final_response", new
        {
            planId,
            subject,
            reply,
            terminalReason,
        });
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

    public static PlanReviewCompletionResult Executed(string reply, PlanExecutionOutcome outcome) => new()
    {
        Kind = PlanReviewCompletionKind.Executed,
        Reply = reply,
        ExecutionOutcome = outcome,
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

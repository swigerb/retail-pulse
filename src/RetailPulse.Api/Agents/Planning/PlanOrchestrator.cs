using System.Text;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Approval;
using RetailPulse.Api.Persistence;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Approval;
using RetailPulse.Contracts.Observability;
using RetailPulse.Contracts.Persistence;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Agents.Planning;

/// <summary>
/// Composition entry point for the plan-first orchestration path. Chained from
/// <see cref="Endpoints.ChatEndpoints"/> only when the router flags a request
/// as multi-domain (<see cref="PlanPersistenceOptions.MinDetectedIntentsForPlan"/>)
/// and every prerequisite (persistence enabled, non-anonymous caller, non-council
/// intent, planner definition present) is satisfied. Owns the two persistence
/// transitions that #93's failure invariants pin: create-plan-then-fail-fast on
/// unusable output, and finalize-plan on terminal outcome.
/// </summary>
public sealed class PlanOrchestrator
{
    private readonly PlanBuilder _builder;
    private readonly PlanExecutor _executor;
    private readonly IPlanStore _planStore;
    private readonly ICostTracker _costTracker;
    private readonly PlanPersistenceOptions _options;
    private readonly ILogger<PlanOrchestrator> _logger;
    private readonly PlanReviewCoordinator? _reviewCoordinator;
    private readonly PlanReviewOptions? _reviewOptions;

    public PlanOrchestrator(
        PlanBuilder builder,
        PlanExecutor executor,
        IPlanStore planStore,
        ICostTracker costTracker,
        PlanPersistenceOptions options,
        ILogger<PlanOrchestrator> logger,
        PlanReviewCoordinator? reviewCoordinator = null,
        IOptions<PlanReviewOptions>? reviewOptions = null)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _planStore = planStore ?? throw new ArgumentNullException(nameof(planStore));
        _costTracker = costTracker ?? throw new ArgumentNullException(nameof(costTracker));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _reviewCoordinator = reviewCoordinator;
        _reviewOptions = reviewOptions?.Value;
    }

    public async Task<PlanOrchestrationResult> RunAsync(
        PlanOrchestrationInput input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        string planId = Guid.NewGuid().ToString("N");
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;

        // Step 1: build the plan.
        PlanBuildResult built = await _builder.BuildAsync(
            input.Request.Message, input.Roster, input.DetectedIntents, ct).ConfigureAwait(false);

        // Plan-level cost attribution (#93): every planner LLM invocation gets
        // its own UsageEvent with PlanId set and PlanStepId = null so the
        // planner call is charged to the plan itself, not silently rolled into
        // an aggregate total or double-counted against a specialist. Emitted
        // even when the plan is unusable — the planner LLM still burned tokens
        // producing that empty-steps response and we owe an honest audit trail.
        await TryTrackPlannerUsageAsync(planId, built, ct).ConfigureAwait(false);

        if (built.IsUnusable)
        {
            // Persist an unusable/failed plan with zero specialists invoked (per #93).
            await _planStore.CreatePlanAsync(new PlanWrite
            {
                PlanId = planId,
                Subject = input.Subject,
                SessionId = input.Request.SessionId,
                TenantId = input.TenantId,
                Request = input.Request.Message,
                DetectedIntents = input.DetectedIntents,
                Status = PlanStatus.Unusable,
                Steps = [],
                CreatedAt = createdAt,
            }, ct).ConfigureAwait(false);

            await _planStore.UpdatePlanStatusAsync(new PlanStatusUpdate
            {
                PlanId = planId,
                Subject = input.Subject,
                Status = PlanStatus.Unusable,
                FailureReason = built.UnusableReason,
                TotalInputTokens = built.InputTokens,
                TotalOutputTokens = built.OutputTokens,
                TotalTokens = built.TotalTokens,
                UpdatedAt = DateTimeOffset.UtcNow,
            }, ct).ConfigureAwait(false);

            _logger.LogWarning(
                "Plan {PlanId} unusable — reason: {Reason}. Falling back with an honest failure reply.",
                planId, built.UnusableReason);

            return new PlanOrchestrationResult(
                PlanId: planId,
                Status: PlanStatus.Unusable,
                Reply: BuildUnusableReply(built.UnusableReason),
                DurationMs: 0,
                InputTokens: built.InputTokens ?? 0,
                OutputTokens: built.OutputTokens ?? 0,
                TotalTokens: built.TotalTokens ?? 0,
                Steps: [],
                FailureReason: built.UnusableReason);
        }

        // Step 2: plan review gate (#94). When enabled, present the plan to a
        // human reviewer before ANY step executes. Approve → execute the
        // original plan; edit → execute the reviewer's edited plan; reject with
        // feedback → the coordinator loops with the planner (bounded rounds);
        // timeout / replan exhausted → terminate the plan without executing.
        // Every path writes an approval row through the same #91 gate so tool
        // and plan approvals share one audit trail.
        //
        // When disabled (default), this block is a no-op and the executor sees
        // the original planner output — the pre-#94 hot path is preserved
        // byte-for-byte.
        PlanBuildResult effectivePlan = built;
        PlanReviewOutcome? reviewOutcome = null;
        if (_reviewOptions is { Enabled: true } && _reviewCoordinator is not null)
        {
            reviewOutcome = await RunReviewAsync(planId, input, built, ct).ConfigureAwait(false);
            if (!reviewOutcome.IsApproved)
            {
                // Persist the awaiting/terminal plan record with the reviewer's
                // decision so /api/plans/{id} shows an honest terminal state.
                await PersistReviewTerminalAsync(planId, input, built, reviewOutcome, createdAt, ct)
                    .ConfigureAwait(false);

                return new PlanOrchestrationResult(
                    PlanId: planId,
                    Status: PlanStatus.Failed,
                    Reply: BuildReviewTerminalReply(reviewOutcome),
                    DurationMs: 0,
                    InputTokens: built.InputTokens ?? 0,
                    OutputTokens: built.OutputTokens ?? 0,
                    TotalTokens: built.TotalTokens ?? 0,
                    Steps: [],
                    FailureReason: reviewOutcome.FailureMessage ?? reviewOutcome.TerminalReason);
            }

            // Approved outcome — swap in the possibly-edited step list before
            // materializing step IDs. Both approve and edit paths flow through
            // the same code so the executor never sees the pre-review plan when
            // the reviewer swapped it out.
            effectivePlan = built with
            {
                Steps = [.. reviewOutcome.FinalSteps.Select(s => new PlannerStep
                {
                    SpecialistKey = s.SpecialistKey,
                    Intent = s.Intent,
                    Action = s.Action,
                })],
            };
        }

        // Step 3: materialize step IDs and persist the initial plan.
        var stepIds = new List<string>(effectivePlan.Steps.Count);
        var stepWrites = new List<PlanStepWrite>(effectivePlan.Steps.Count);
        for (int i = 0; i < effectivePlan.Steps.Count; i++)
        {
            string stepId = $"{planId}-s{i}";
            stepIds.Add(stepId);
            stepWrites.Add(new PlanStepWrite
            {
                StepId = stepId,
                StepIndex = i,
                SpecialistKey = effectivePlan.Steps[i].SpecialistKey,
                Intent = effectivePlan.Steps[i].Intent,
                Action = effectivePlan.Steps[i].Action,
                Status = PlanStepStatus.Pending,
            });
        }

        await _planStore.CreatePlanAsync(new PlanWrite
        {
            PlanId = planId,
            Subject = input.Subject,
            SessionId = input.Request.SessionId,
            TenantId = input.TenantId,
            Request = input.Request.Message,
            DetectedIntents = input.DetectedIntents,
            Status = PlanStatus.Running,
            Steps = stepWrites,
            CreatedAt = createdAt,
        }, ct).ConfigureAwait(false);

        // Step 4: execute the (possibly edited) workflow.
        var executionRequest = new PlanExecutionRequest
        {
            PlanId = planId,
            Subject = input.Subject,
            PrincipalKey = input.PrincipalKey,
            SessionId = input.Request.SessionId,
            TraceId = input.TraceId,
            ParentSpanId = input.ParentSpanId,
            Request = input.Request.Message,
            History = input.Request.History,
            User = input.Request.User,
            Plan = effectivePlan,
            StepIds = stepIds,
            SpecialistLookup = input.SpecialistLookup,
        };

        PlanExecutionOutcome outcome = await _executor.ExecuteAsync(executionRequest, ct).ConfigureAwait(false);

        return new PlanOrchestrationResult(
            PlanId: planId,
            Status: outcome.Status,
            Reply: BuildFinalReply(outcome),
            DurationMs: outcome.DurationMs,
            InputTokens: (effectivePlan.InputTokens ?? 0) + outcome.Steps.Sum(s => s.InputTokens),
            OutputTokens: (effectivePlan.OutputTokens ?? 0) + outcome.Steps.Sum(s => s.OutputTokens),
            TotalTokens: (effectivePlan.TotalTokens ?? 0) + outcome.Steps.Sum(s => s.TotalTokens),
            Steps: outcome.Steps,
            FailureReason: outcome.FailureReason);
    }

    private async Task<PlanReviewOutcome> RunReviewAsync(
        string planId,
        PlanOrchestrationInput input,
        PlanBuildResult built,
        CancellationToken ct)
    {
        var initialSteps = built.Steps
            .Select(s => new PlanReviewStepDto
            {
                SpecialistKey = s.SpecialistKey,
                Intent = s.Intent,
                Action = s.Action,
            })
            .ToList();

        var specialistKeys = input.Roster
            .Select(a => a.Key)
            .ToList();

        var reviewInput = new PlanReviewCoordinationInput
        {
            PlanId = planId,
            Subject = input.Subject,
            SessionId = input.Request.SessionId,
            Request = input.Request.Message,
            InitialSteps = initialSteps,
            SpecialistKeys = specialistKeys,
            Roster = input.Roster,
            DetectedIntents = input.DetectedIntents,
        };

        return await _reviewCoordinator!.CoordinateAsync(reviewInput, ct).ConfigureAwait(false);
    }

    private async Task PersistReviewTerminalAsync(
        string planId,
        PlanOrchestrationInput input,
        PlanBuildResult built,
        PlanReviewOutcome outcome,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        // Record the plan as AwaitingReview → Failed so audit history preserves
        // the intended step list and the terminal reason. Steps stay Pending on
        // disk (they never ran).
        var stepWrites = new List<PlanStepWrite>(built.Steps.Count);
        for (int i = 0; i < built.Steps.Count; i++)
        {
            stepWrites.Add(new PlanStepWrite
            {
                StepId = $"{planId}-s{i}",
                StepIndex = i,
                SpecialistKey = built.Steps[i].SpecialistKey,
                Intent = built.Steps[i].Intent,
                Action = built.Steps[i].Action,
                Status = PlanStepStatus.Pending,
            });
        }

        await _planStore.CreatePlanAsync(new PlanWrite
        {
            PlanId = planId,
            Subject = input.Subject,
            SessionId = input.Request.SessionId,
            TenantId = input.TenantId,
            Request = input.Request.Message,
            DetectedIntents = input.DetectedIntents,
            Status = PlanStatus.AwaitingReview,
            Steps = stepWrites,
            CreatedAt = createdAt,
        }, ct).ConfigureAwait(false);

        await _planStore.UpdatePlanStatusAsync(new PlanStatusUpdate
        {
            PlanId = planId,
            Subject = input.Subject,
            Status = PlanStatus.Failed,
            FailureReason = $"{outcome.TerminalReason}: {outcome.FailureMessage}",
            TotalInputTokens = built.InputTokens,
            TotalOutputTokens = built.OutputTokens,
            TotalTokens = built.TotalTokens,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);

        _logger.LogWarning(
            "Plan {PlanId} terminated at review: {Terminal} — {Message}",
            planId, outcome.TerminalReason, outcome.FailureMessage);
    }

    private static string BuildReviewTerminalReply(PlanReviewOutcome outcome) =>
        outcome.TerminalReason switch
        {
            PlanReviewTerminalReason.ReviewTimedOut =>
                "The plan was not executed because reviewer approval timed out.",
            PlanReviewTerminalReason.ReplanExhausted =>
                "The plan was not executed because the reviewer rejected every revision within the configured limit.",
            PlanReviewTerminalReason.EditedToEmpty =>
                "The plan was not executed because the reviewer edited it down to zero steps.",
            PlanReviewTerminalReason.EditInvalid =>
                "The plan was not executed because the reviewer's edited step list referenced an unknown specialist.",
            _ =>
                "The plan was not executed because the reviewer declined to approve it.",
        };

    private static string BuildFinalReply(PlanExecutionOutcome outcome)
    {
        // Compose a plain-text reply that stitches together each completed
        // step's specialist reply, preserving order. The frontend / trace can
        // still render the individual step results if it wants more detail.
        var sb = new StringBuilder();

        foreach (PlanStepResult step in outcome.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Result))
                continue;
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

    private static string BuildUnusableReply(string? reason)
    {
        // Deliberately blunt: the planner declined to produce a plan. Surface
        // the reason if we have one so the caller sees a real diagnostic.
        return string.IsNullOrWhiteSpace(reason)
            ? "The plan-first orchestrator did not produce a usable plan for this request."
            : $"The plan-first orchestrator did not produce a usable plan: {reason}.";
    }

    private async Task TryTrackPlannerUsageAsync(
        string planId,
        PlanBuildResult built,
        CancellationToken ct)
    {
        int input = built.InputTokens ?? 0;
        int output = built.OutputTokens ?? 0;
        if (input == 0 && output == 0)
        {
            // No planner call happened (e.g. empty-roster short-circuit before
            // the LLM), so there is nothing to attribute. Skipping keeps the
            // cost feed honest — do not fabricate a zero-token event.
            return;
        }

        string model = string.IsNullOrWhiteSpace(built.Model) ? "planner" : built.Model;
        try
        {
            await _costTracker.TrackUsageAsync(new UsageEvent(
                AgentId: "planner",
                Model: model,
                InputTokens: input,
                OutputTokens: output,
                ToolName: null,
                Timestamp: DateTime.UtcNow,
                CacheHit: false,
                PlanId: planId,
                PlanStepId: null), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Cost attribution never fails the plan. Log and move on so the
            // caller still gets the plan reply.
            _logger.LogWarning(
                ex,
                "Failed to record plan-level planner usage for plan {PlanId}.",
                planId);
        }
    }
}

/// <summary>Input envelope for <see cref="PlanOrchestrator.RunAsync"/>.</summary>
public sealed record PlanOrchestrationInput
{
    public required ChatRequest Request { get; init; }
    public required string Subject { get; init; }
    public required string PrincipalKey { get; init; }
    public string? TenantId { get; init; }
    public required IReadOnlyList<ISpecialistAgent> Roster { get; init; }
    public required IReadOnlyDictionary<string, ISpecialistAgent> SpecialistLookup { get; init; }
    public required IReadOnlyList<string> DetectedIntents { get; init; }
    public required string TraceId { get; init; }
    public string? ParentSpanId { get; init; }
}

/// <summary>Terminal result returned to the chat endpoint.</summary>
public sealed record PlanOrchestrationResult(
    string PlanId,
    string Status,
    string Reply,
    long DurationMs,
    int InputTokens,
    int OutputTokens,
    int TotalTokens,
    IReadOnlyList<PlanStepResult> Steps,
    string? FailureReason);

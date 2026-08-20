using System.Text;
using RetailPulse.Api.Persistence;
using RetailPulse.Contracts;
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
    private readonly PlanPersistenceOptions _options;
    private readonly ILogger<PlanOrchestrator> _logger;

    public PlanOrchestrator(
        PlanBuilder builder,
        PlanExecutor executor,
        IPlanStore planStore,
        PlanPersistenceOptions options,
        ILogger<PlanOrchestrator> logger)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _planStore = planStore ?? throw new ArgumentNullException(nameof(planStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        // Step 2: materialize step IDs and persist the initial plan.
        var stepIds = new List<string>(built.Steps.Count);
        var stepWrites = new List<PlanStepWrite>(built.Steps.Count);
        for (int i = 0; i < built.Steps.Count; i++)
        {
            string stepId = $"{planId}-s{i}";
            stepIds.Add(stepId);
            stepWrites.Add(new PlanStepWrite
            {
                StepId = stepId,
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
            Status = PlanStatus.Running,
            Steps = stepWrites,
            CreatedAt = createdAt,
        }, ct).ConfigureAwait(false);

        // Step 3: execute the workflow.
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
            Plan = built,
            StepIds = stepIds,
            SpecialistLookup = input.SpecialistLookup,
        };

        PlanExecutionOutcome outcome = await _executor.ExecuteAsync(executionRequest, ct).ConfigureAwait(false);

        return new PlanOrchestrationResult(
            PlanId: planId,
            Status: outcome.Status,
            Reply: BuildFinalReply(outcome),
            DurationMs: outcome.DurationMs,
            InputTokens: (built.InputTokens ?? 0) + outcome.Steps.Sum(s => s.InputTokens),
            OutputTokens: (built.OutputTokens ?? 0) + outcome.Steps.Sum(s => s.OutputTokens),
            TotalTokens: (built.TotalTokens ?? 0) + outcome.Steps.Sum(s => s.TotalTokens),
            Steps: outcome.Steps,
            FailureReason: outcome.FailureReason);
    }

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

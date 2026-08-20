using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Agents.AI.Workflows;
using RetailPulse.Api.Budget;
using RetailPulse.Api.Charts;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Persistence;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Observability;
using RetailPulse.Contracts.Persistence;
using RetailPulse.Contracts.Routing;
using RetailPulse.Contracts.Tracing;

namespace RetailPulse.Api.Agents.Planning;

/// <summary>
/// Runs a validated <see cref="PlanBuildResult"/> as a Microsoft.Agents.AI.Workflows
/// workflow — one <see cref="FunctionExecutor{T}"/> per step, chained sequentially.
/// Executes on <see cref="InProcessExecution"/> with an
/// <see cref="CheckpointManager"/> so #94 (suspend/resume) can build on real
/// framework checkpoints instead of a bespoke scheduler.
/// <para>
/// Contract highlights that #93 pins:
/// </para>
/// <list type="bullet">
///   <item>A single <see cref="RequestToolContext.Begin"/> scope encloses the whole plan
///     — the specialist pipeline's own budget-scope call is a no-op inside this
///     outer scope (see <c>RequestToolContext.Begin</c>) so ADR-006's tool-context
///     budget accumulates cumulatively across all steps.</item>
///   <item>Every step invocation persists a running/completed/failed/timed_out
///     transition through <see cref="IPlanStore"/> so a mid-plan crash leaves an
///     honest terminal record instead of a "running-forever" ghost.</item>
///   <item>Per-step and plan-level cost events attribute tokens back to
///     <c>plan_id</c> and <c>plan_step_id</c> for post-hoc reconciliation.</item>
///   <item>Trace spans for the plan and each step carry
///     <c>span.type = plan</c> / <c>plan_step</c>, joining the existing conventions.</item>
/// </list>
/// </summary>
public sealed class PlanExecutor
{
    private readonly IPlanStore _planStore;
    private readonly ICostTracker _costTracker;
    private readonly ITraceCollector _traceCollector;
    private readonly PlanPersistenceOptions _options;
    private readonly ILogger<PlanExecutor> _logger;

    public PlanExecutor(
        IPlanStore planStore,
        ICostTracker costTracker,
        ITraceCollector traceCollector,
        PlanPersistenceOptions options,
        ILogger<PlanExecutor> logger)
    {
        _planStore = planStore ?? throw new ArgumentNullException(nameof(planStore));
        _costTracker = costTracker ?? throw new ArgumentNullException(nameof(costTracker));
        _traceCollector = traceCollector ?? throw new ArgumentNullException(nameof(traceCollector));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Execute an already-validated plan against the caller-supplied specialist
    /// lookup. Returns a <see cref="PlanExecutionOutcome"/> that includes final
    /// per-step transcripts and terminal plan status. Persistence and telemetry
    /// happen inline so a mid-flight cancel still leaves an honest record.
    /// </summary>
    public async Task<PlanExecutionOutcome> ExecuteAsync(
        PlanExecutionRequest execution,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var stepResults = new List<PlanStepResult>(execution.Plan.Steps.Count);
        var planSw = Stopwatch.StartNew();
        DateTimeOffset planStart = DateTimeOffset.UtcNow;

        // Build a workflow. Sequential linear graph: each executor takes the
        // accumulated context, invokes the specialist, appends its result, and
        // either forwards to the next step or yields the final output.
        var executors = new List<FunctionExecutor<PlanStepMessage>>(execution.Plan.Steps.Count);
        for (int i = 0; i < execution.Plan.Steps.Count; i++)
        {
            int stepIndex = i;
            PlannerStep planned = execution.Plan.Steps[stepIndex];
            string stepId = execution.StepIds[stepIndex];
            string executorId = $"plan-{execution.PlanId}-step-{stepIndex}";

            executors.Add(new FunctionExecutor<PlanStepMessage>(
                id: executorId,
                handlerAsync: async (message, context, stepCt) =>
                {
                    PlanStepResult result = await RunOneStepAsync(
                        execution, planned, stepIndex, stepId, message, stepCt).ConfigureAwait(false);
                    stepResults.Add(result);

                    bool shouldContinue = string.Equals(result.Status, PlanStepStatus.Completed, StringComparison.Ordinal);
                    bool isLast = stepIndex == execution.Plan.Steps.Count - 1;

                    if (!shouldContinue || isLast)
                    {
                        // Terminal — hand the accumulated results out through the workflow output stream.
                        await context.YieldOutputAsync(new PlanTerminalMessage(result.Status), stepCt).ConfigureAwait(false);
                        return;
                    }

                    // Forward to the next step; propagate the accumulated transcript.
                    var next = new PlanStepMessage(
                        Request: message.Request,
                        History: message.History,
                        User: message.User,
                        AccumulatedResults: [.. message.AccumulatedResults, result]);

                    // targetId=null broadcasts the message to every connected
                    // executor via the edge set below. Since we build a strictly
                    // sequential graph (i -> i+1), this delivers exactly to the
                    // next step.
                    await context.SendMessageAsync(next, targetId: null, stepCt).ConfigureAwait(false);
                },
                options: null,
                sentMessageTypes: [typeof(PlanStepMessage)],
                outputTypes: [typeof(PlanTerminalMessage)]));
        }

        WorkflowBuilder builder = new(executors[0]);
        for (int i = 0; i < executors.Count - 1; i++)
        {
            builder.AddEdge(executors[i], executors[i + 1]);
        }
        Workflow workflow = builder.Build();

        // Root plan span.
        Activity? planActivity = AgentTelemetry.Source.StartActivity("plan.execute", ActivityKind.Internal);
        planActivity?.SetTag("plan.id", execution.PlanId);
        planActivity?.SetTag("plan.step_count", execution.Plan.Steps.Count);

        // Enclose the whole plan in ONE budget scope. RequestToolContext.Begin
        // is idempotent w.r.t. an existing scope (see the nesting comment
        // there), so per-step specialist invocations that also call Begin
        // will reuse this outer scope rather than resetting the counters.
        //
        // ADR-006 chart-intent preservation (#93): compute the explicit chart
        // request flag ONCE from the raw user request and set it on the outer
        // plan scope. Nested per-step Begin() calls in specialist agents will
        // detect the outer scope and reuse it via NestedScope (see
        // RequestToolContext.Begin), so every step across a multi-domain chart
        // request sees the tighter chart cap — not just whichever specialist
        // opens the scope first, which would silently lose chart intent for
        // the rest of the plan.
        bool planIsChartIntent = ChartRequestDetector.Detect(execution.Request).IsExplicitChartRequest;
        using IDisposable planBudget = RequestToolContext.Begin(
            execution.PrincipalKey,
            isChartIntent: planIsChartIntent);

        // Overall plan timeout: prevents a rogue specialist from stalling the
        // whole request forever if the per-step timeout was set generously.
        using var planCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_options.PlanTimeout > TimeSpan.Zero)
            planCts.CancelAfter(_options.PlanTimeout);

        string terminalStatus = PlanStatus.Completed;
        string? failureReason = null;

        try
        {
            var initial = new PlanStepMessage(
                Request: execution.Request,
                History: execution.History,
                User: execution.User,
                AccumulatedResults: []);

            var checkpointManager = CheckpointManager.CreateInMemory();
            Run run = await InProcessExecution
                .RunAsync(workflow, initial, checkpointManager, execution.PlanId, planCts.Token)
                .ConfigureAwait(false);
            // The workflow yields a PlanTerminalMessage; we already track terminality
            // from the step results, so simply reading run status is enough.
            _ = await run.GetStatusAsync(planCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (planCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            terminalStatus = PlanStatus.Failed;
            failureReason = "plan exceeded overall timeout";
            _logger.LogWarning("Plan {PlanId} timed out after {Timeout}.", execution.PlanId, _options.PlanTimeout);
        }
        catch (OperationCanceledException)
        {
            terminalStatus = PlanStatus.Cancelled;
            failureReason = "cancelled by caller";
            throw;
        }
        catch (Exception ex)
        {
            terminalStatus = PlanStatus.Failed;
            failureReason = ex.Message;
            _logger.LogError(ex, "Plan {PlanId} execution failed.", execution.PlanId);
        }
        finally
        {
            // Derive the plan's terminal status from the final observed step
            // when we didn't crash out early.
            if (failureReason is null && stepResults.Count > 0)
            {
                PlanStepResult last = stepResults[^1];
                terminalStatus = last.Status switch
                {
                    PlanStepStatus.Completed => PlanStatus.Completed,
                    PlanStepStatus.Failed => PlanStatus.Failed,
                    PlanStepStatus.TimedOut => PlanStatus.Failed,
                    PlanStepStatus.Cancelled => PlanStatus.Cancelled,
                    PlanStepStatus.Unusable => PlanStatus.Unusable,
                    _ => PlanStatus.Completed,
                };
                if (!string.Equals(terminalStatus, PlanStatus.Completed, StringComparison.Ordinal))
                    failureReason ??= last.Error ?? $"step {last.StepIndex} ended in {last.Status}";
            }

            // Any steps we haven't yet observed must have been skipped by the workflow halt.
            for (int i = stepResults.Count; i < execution.Plan.Steps.Count; i++)
            {
                await _planStore.UpdateStepAsync(new PlanStepUpdate
                {
                    StepId = execution.StepIds[i],
                    PlanId = execution.PlanId,
                    Subject = execution.Subject,
                    Status = PlanStepStatus.Skipped,
                    CompletedAt = DateTimeOffset.UtcNow,
                }, CancellationToken.None).ConfigureAwait(false);
            }

            planSw.Stop();
            int totalIn = stepResults.Sum(r => r.InputTokens);
            int totalOut = stepResults.Sum(r => r.OutputTokens);
            int totalTokens = stepResults.Sum(r => r.TotalTokens);

            await _planStore.UpdatePlanStatusAsync(new PlanStatusUpdate
            {
                PlanId = execution.PlanId,
                Subject = execution.Subject,
                Status = terminalStatus,
                FailureReason = failureReason,
                TotalInputTokens = totalIn,
                TotalOutputTokens = totalOut,
                TotalTokens = totalTokens,
                TotalDurationMs = planSw.ElapsedMilliseconds,
                UpdatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None).ConfigureAwait(false);

            EmitPlanSpan(execution, planStart, planSw, terminalStatus, totalTokens);
            planActivity?.SetTag("plan.status", terminalStatus);
            planActivity?.Dispose();
        }

        return new PlanExecutionOutcome(
            PlanId: execution.PlanId,
            Status: terminalStatus,
            FailureReason: failureReason,
            Steps: stepResults,
            DurationMs: planSw.ElapsedMilliseconds);
    }

    private async Task<PlanStepResult> RunOneStepAsync(
        PlanExecutionRequest execution,
        PlannerStep planned,
        int stepIndex,
        string stepId,
        PlanStepMessage message,
        CancellationToken workflowCt)
    {
        DateTimeOffset stepStart = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // Look up the specialist afresh at execution time so the roster the
        // planner saw and the roster we dispatch through are the same one.
        if (!execution.SpecialistLookup.TryGetValue(planned.SpecialistKey, out ISpecialistAgent? specialist))
        {
            await _planStore.UpdateStepAsync(new PlanStepUpdate
            {
                StepId = stepId,
                PlanId = execution.PlanId,
                Subject = execution.Subject,
                Status = PlanStepStatus.Unusable,
                Error = $"specialist '{planned.SpecialistKey}' is no longer registered",
                StartedAt = stepStart,
                CompletedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None).ConfigureAwait(false);

            return new PlanStepResult(
                stepIndex, stepId, planned.SpecialistKey, planned.Intent, planned.Action,
                PlanStepStatus.Unusable, "", $"specialist '{planned.SpecialistKey}' not registered",
                0, 0, 0, sw.ElapsedMilliseconds);
        }

        // Mark running.
        await _planStore.UpdateStepAsync(new PlanStepUpdate
        {
            StepId = stepId,
            PlanId = execution.PlanId,
            Subject = execution.Subject,
            Status = PlanStepStatus.Running,
            StartedAt = stepStart,
        }, CancellationToken.None).ConfigureAwait(false);

        // Weave the action into the request so the specialist sees a scoped
        // sub-question rather than the raw multi-domain message. Prior step
        // transcripts are appended to the history so later steps can build on
        // earlier evidence.
        string stepMessage = string.IsNullOrWhiteSpace(planned.Action)
            ? message.Request
            : $"{planned.Action} — original user request: {message.Request}";

        var stepHistory = new List<ChatHistoryMessage>();
        if (message.History is { Count: > 0 })
            stepHistory.AddRange(message.History);
        foreach (PlanStepResult prior in message.AccumulatedResults)
        {
            if (!string.IsNullOrWhiteSpace(prior.Result))
            {
                stepHistory.Add(new ChatHistoryMessage(
                    "assistant",
                    $"[{prior.SpecialistKey}] {prior.Result}"));
            }
        }

        var scopedRequest = new ChatRequest(
            Message: stepMessage,
            SessionId: execution.SessionId,
            User: message.User,
            History: stepHistory);

        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(workflowCt);
        if (_options.StepTimeout > TimeSpan.Zero)
            stepCts.CancelAfter(_options.StepTimeout);

        ChatResponse? response = null;
        string status = PlanStepStatus.Completed;
        string? error = null;

        try
        {
            response = await specialist.HandleAsync(scopedRequest, stepCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stepCts.IsCancellationRequested && !workflowCt.IsCancellationRequested)
        {
            status = PlanStepStatus.TimedOut;
            error = $"step {stepIndex} timed out after {_options.StepTimeout}";
            _logger.LogWarning(
                "Plan {PlanId} step {StepIndex} ({Key}) timed out after {Timeout}.",
                execution.PlanId, stepIndex, planned.SpecialistKey, _options.StepTimeout);
        }
        catch (OperationCanceledException)
        {
            status = PlanStepStatus.Cancelled;
            error = "cancelled";
            throw;
        }
        catch (Exception ex)
        {
            status = PlanStepStatus.Failed;
            error = ex.Message;
            _logger.LogError(
                ex, "Plan {PlanId} step {StepIndex} ({Key}) failed.",
                execution.PlanId, stepIndex, planned.SpecialistKey);
        }
        finally
        {
            sw.Stop();
        }

        int input = response?.TokenUsage?.InputTokens ?? 0;
        int output = response?.TokenUsage?.OutputTokens ?? 0;
        int total = response?.TokenUsage?.TotalTokens ?? (input + output);

        await _planStore.UpdateStepAsync(new PlanStepUpdate
        {
            StepId = stepId,
            PlanId = execution.PlanId,
            Subject = execution.Subject,
            Status = status,
            Result = response?.Reply,
            Error = error,
            InputTokens = input,
            OutputTokens = output,
            TotalTokens = total,
            DurationMs = sw.ElapsedMilliseconds,
            CompletedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None).ConfigureAwait(false);

        // Attribute usage to the step (and plan) so audit reconciles.
        try
        {
            await _costTracker.TrackUsageAsync(new UsageEvent(
                AgentId: specialist.Key,
                Model: specialist.Model,
                InputTokens: input,
                OutputTokens: output,
                ToolName: null,
                Timestamp: DateTime.UtcNow,
                CacheHit: false,
                PlanId: execution.PlanId,
                PlanStepId: stepId), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record cost for plan {PlanId} step {StepIndex}.", execution.PlanId, stepIndex);
        }

        EmitStepSpan(execution, stepIndex, stepId, planned, status, stepStart, sw, input, output);

        return new PlanStepResult(
            stepIndex, stepId, planned.SpecialistKey, planned.Intent, planned.Action,
            status, response?.Reply ?? "", error, input, output, total, sw.ElapsedMilliseconds);
    }

    private void EmitPlanSpan(
        PlanExecutionRequest execution,
        DateTimeOffset planStart,
        Stopwatch planSw,
        string status,
        int totalTokens)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["span.type"] = "plan",
            ["plan.id"] = execution.PlanId,
            ["plan.status"] = status,
            ["plan.step_count"] = execution.Plan.Steps.Count.ToString(CultureInfo.InvariantCulture),
        };

        _traceCollector.CaptureSpan(new TraceSpan(
            SpanId: Guid.NewGuid().ToString("N")[..16],
            TraceId: execution.TraceId,
            ParentSpanId: execution.ParentSpanId,
            OperationName: "plan.execute",
            StartTime: planStart,
            EndTime: DateTimeOffset.UtcNow,
            DurationMs: planSw.Elapsed.TotalMilliseconds,
            InputTokens: 0,
            OutputTokens: totalTokens,
            EstimatedCostUsd: 0m,
            Tags: tags));
    }

    private void EmitStepSpan(
        PlanExecutionRequest execution,
        int stepIndex,
        string stepId,
        PlannerStep planned,
        string status,
        DateTimeOffset stepStart,
        Stopwatch sw,
        int input,
        int output)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["span.type"] = "plan_step",
            ["plan.id"] = execution.PlanId,
            ["plan.step_id"] = stepId,
            ["plan.step_index"] = stepIndex.ToString(CultureInfo.InvariantCulture),
            ["plan.step_status"] = status,
            ["plan.step_specialist"] = planned.SpecialistKey,
            ["plan.step_intent"] = planned.Intent,
        };

        _traceCollector.CaptureSpan(new TraceSpan(
            SpanId: Guid.NewGuid().ToString("N")[..16],
            TraceId: execution.TraceId,
            ParentSpanId: execution.ParentSpanId,
            OperationName: $"plan.step.{planned.SpecialistKey}",
            StartTime: stepStart,
            EndTime: DateTimeOffset.UtcNow,
            DurationMs: sw.Elapsed.TotalMilliseconds,
            InputTokens: input,
            OutputTokens: output,
            EstimatedCostUsd: 0m,
            Tags: tags));
    }
}

/// <summary>Input envelope for <see cref="PlanExecutor.ExecuteAsync"/>.</summary>
public sealed record PlanExecutionRequest
{
    public required string PlanId { get; init; }
    public required string Subject { get; init; }
    public required string PrincipalKey { get; init; }
    public string? SessionId { get; init; }
    public required string TraceId { get; init; }
    public string? ParentSpanId { get; init; }
    public required string Request { get; init; }
    public IReadOnlyList<ChatHistoryMessage>? History { get; init; }
    public UserContext? User { get; init; }
    public required PlanBuildResult Plan { get; init; }
    public required IReadOnlyList<string> StepIds { get; init; }
    public required IReadOnlyDictionary<string, ISpecialistAgent> SpecialistLookup { get; init; }
}

/// <summary>Terminal outcome returned to the chat endpoint.</summary>
public sealed record PlanExecutionOutcome(
    string PlanId,
    string Status,
    string? FailureReason,
    IReadOnlyList<PlanStepResult> Steps,
    long DurationMs);

/// <summary>Per-step transcript captured during workflow execution.</summary>
public sealed record PlanStepResult(
    int StepIndex,
    string StepId,
    string SpecialistKey,
    string Intent,
    string Action,
    string Status,
    string Result,
    string? Error,
    int InputTokens,
    int OutputTokens,
    int TotalTokens,
    long DurationMs);

/// <summary>Message carried between step executors on the workflow bus.</summary>
public sealed record PlanStepMessage(
    string Request,
    IReadOnlyList<ChatHistoryMessage>? History,
    UserContext? User,
    IReadOnlyList<PlanStepResult> AccumulatedResults);

/// <summary>Terminal signal yielded to the workflow output stream.</summary>
public sealed record PlanTerminalMessage(string TerminalStepStatus);

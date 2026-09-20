using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Api.Agents.Planning;
using RetailPulse.Api.Budget;
using RetailPulse.Api.Observability;
using RetailPulse.Api.Persistence;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Observability;
using RetailPulse.Contracts.Persistence;
using RetailPulse.Contracts.Routing;
using RetailPulse.Contracts.Tracing;

namespace RetailPulse.Tests.Planning;

/// <summary>
/// Deterministic tests for <see cref="PlanExecutor"/>. Every dependency is
/// mocked so we can pin the exact contracts #93 requires: cumulative
/// tool-context budget across steps, span types, failed-step short-circuit,
/// step-timeout persistence, and correct usage attribution.
/// </summary>
public sealed class PlanExecutorTests
{
    private sealed class RecordingPlanStore : IPlanStore
    {
        public ConcurrentQueue<PlanWrite> Creates { get; } = new();
        public ConcurrentQueue<PlanStatusUpdate> StatusUpdates { get; } = new();
        public ConcurrentQueue<PlanStepUpdate> StepUpdates { get; } = new();

        public Task CreatePlanAsync(PlanWrite plan, CancellationToken ct = default)
        {
            Creates.Enqueue(plan);
            return Task.CompletedTask;
        }

        public Task UpdatePlanStatusAsync(PlanStatusUpdate update, CancellationToken ct = default)
        {
            StatusUpdates.Enqueue(update);
            return Task.CompletedTask;
        }

        public Task UpdateStepAsync(PlanStepUpdate update, CancellationToken ct = default)
        {
            StepUpdates.Enqueue(update);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PlanSummaryDto>> ListPlansForSubjectAsync(string subject, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PlanSummaryDto>>([]);

        public Task<PlanDetailDto?> GetPlanAsync(string subject, string planId, CancellationToken ct = default)
            => Task.FromResult<PlanDetailDto?>(null);

        public Task<bool> DeletePlanAsync(string subject, string planId, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<PlanCleanupResult> PurgeExpiredAsync(DateTimeOffset olderThan, CancellationToken ct = default)
            => Task.FromResult(new PlanCleanupResult(0, 0));
    }

    private sealed class RecordingTraceCollector : ITraceCollector
    {
        public ConcurrentQueue<TraceSpan> Spans { get; } = new();
        public void CaptureSpan(TraceSpan span) => Spans.Enqueue(span);
        public IReadOnlyList<TraceSpan>? GetSpans(string traceId) => null;
        public TraceSummary? GetSummary(string traceId) => null;
        public IReadOnlyList<TraceSummary> GetRecentTraces(int count = 20) => [];
        public StructuredTraceSummary? GetStructuredSummary(string traceId) => null;
        public IReadOnlyList<ToolUsageStat> GetToolStats(DateTimeOffset since, int top = 10) => [];
        public int TraceCount => Spans.Count;
        public int Capacity => 100;
    }

    private sealed class RecordingCostTracker : ICostTracker
    {
        public ConcurrentQueue<UsageEvent> Usages { get; } = new();
        public Task TrackUsageAsync(UsageEvent usage, CancellationToken ct = default)
        {
            Usages.Enqueue(usage);
            return Task.CompletedTask;
        }
        public Task<CostSummary> GetSummaryAsync(CostPeriod period, CancellationToken ct = default)
            => Task.FromResult(new CostSummary(0, 0, 0, period));
        public Task<IReadOnlyList<AgentCostBreakdown>> GetByAgentAsync(CostPeriod period, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentCostBreakdown>>([]);
        public Task<CostTrend> GetTrendAsync(int days = 7, CancellationToken ct = default)
            => Task.FromResult(new CostTrend([]));
    }

    private static ISpecialistAgent MakeSpecialist(
        string key,
        string reply,
        int inputTokens = 100,
        int outputTokens = 50,
        Action? onInvoke = null,
        TimeSpan? delay = null,
        Exception? throwException = null,
        string model = "gpt-test")
    {
        var mock = new Mock<ISpecialistAgent>();
        mock.SetupGet(a => a.Key).Returns(key);
        mock.SetupGet(a => a.DisplayName).Returns(key);
        mock.SetupGet(a => a.Model).Returns(model);
        mock.SetupGet(a => a.SupportedIntents).Returns([key]);
        mock
            .Setup(a => a.HandleAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (ChatRequest _, CancellationToken ct) =>
            {
                onInvoke?.Invoke();
                if (delay is { } d) await Task.Delay(d, ct);
                return throwException is not null
                    ? throw throwException
                    : new ChatResponse(
                        reply,
                        "session-x",
                        [],
                        null,
                        10,
                        new TokenUsage(inputTokens, outputTokens, inputTokens + outputTokens));
            });
        return mock.Object;
    }

    private static PlanExecutor NewExecutor(
        RecordingPlanStore store,
        RecordingCostTracker cost,
        RecordingTraceCollector traces,
        PlanPersistenceOptions? options = null,
        TokenPricing? pricing = null)
    {
        return new PlanExecutor(
            store, cost, traces,
            options ?? new PlanPersistenceOptions(),
            NullLogger<PlanExecutor>.Instance,
            clarifier: null,
            reviewCoordinator: null,
            cancellationRegistry: null,
            pricing: pricing ?? TokenPricing.FromConfiguration(new ConfigurationBuilder().Build()));
    }

    private static PlanExecutionRequest MakeExecutionRequest(
        Dictionary<string, ISpecialistAgent> lookup,
        params (string key, string intent, string action)[] steps)
        => MakeExecutionRequest(lookup, "Do the multi-domain thing.", steps);

    private static PlanExecutionRequest MakeExecutionRequest(
        Dictionary<string, ISpecialistAgent> lookup,
        string requestMessage,
        params (string key, string intent, string action)[] steps)
    {
        var planned = steps.Select(s => new PlannerStep
        {
            SpecialistKey = s.key,
            Intent = s.intent,
            Action = s.action,
        }).ToList();

        var stepIds = planned.Select((_, i) => $"planZ-s{i}").ToList();

        return new PlanExecutionRequest
        {
            PlanId = "planZ",
            Subject = "user-1",
            PrincipalKey = "user-1",
            SessionId = "session-x",
            TraceId = "trace-x",
            ParentSpanId = null,
            Request = requestMessage,
            History = null,
            User = new UserContext("user-1", "User One", string.Empty),
            Plan = new PlanBuildResult { Steps = planned },
            StepIds = stepIds,
            SpecialistLookup = lookup,
        };
    }

    [Fact]
    public async Task Executor_emits_plan_and_plan_step_spans_with_correct_span_type()
    {
        var store = new RecordingPlanStore();
        var cost = new RecordingCostTracker();
        var traces = new RecordingTraceCollector();
        PlanExecutor executor = NewExecutor(store, cost, traces);

        var lookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["scorecard"] = MakeSpecialist("scorecard", "scorecard-reply"),
            ["demand-forecasting"] = MakeSpecialist("demand-forecasting", "demand-reply"),
        };

        PlanExecutionRequest request = MakeExecutionRequest(lookup,
            ("scorecard", "scorecard", "summarize"),
            ("demand-forecasting", "demand", "forecast"));

        PlanExecutionOutcome outcome = await executor.ExecuteAsync(request, CancellationToken.None);

        outcome.Status.Should().Be(PlanStatus.Completed);
        outcome.Steps.Should().HaveCount(2);

        TraceSpan[] spans = [.. traces.Spans];
        spans.Where(s => s.Tags?["span.type"] == "plan").Should().HaveCount(1);
        spans.Where(s => s.Tags?["span.type"] == "plan_step").Should().HaveCount(2);

        TraceSpan planSpan = spans.Single(s => s.Tags?["span.type"] == "plan");
        planSpan.OperationName.Should().Be("plan.execute");
        planSpan.Tags!["plan.id"].Should().Be("planZ");
        planSpan.Tags["plan.status"].Should().Be(PlanStatus.Completed);

        TraceSpan[] stepSpans = [.. spans.Where(s => s.Tags?["span.type"] == "plan_step")];
        stepSpans[0].Tags!["plan.step_specialist"].Should().Be("scorecard");
        stepSpans[1].Tags!["plan.step_specialist"].Should().Be("demand-forecasting");
    }

    [Fact]
    public async Task Cost_events_are_attributed_to_plan_and_step()
    {
        var store = new RecordingPlanStore();
        var cost = new RecordingCostTracker();
        var traces = new RecordingTraceCollector();
        PlanExecutor executor = NewExecutor(store, cost, traces);

        var lookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["scorecard"] = MakeSpecialist("scorecard", "a", inputTokens: 111, outputTokens: 22),
            ["demand-forecasting"] = MakeSpecialist("demand-forecasting", "b", inputTokens: 333, outputTokens: 44),
        };
        PlanExecutionRequest request = MakeExecutionRequest(lookup,
            ("scorecard", "scorecard", "s"),
            ("demand-forecasting", "demand", "d"));

        _ = await executor.ExecuteAsync(request, CancellationToken.None);

        UsageEvent[] usages = [.. cost.Usages];
        usages.Should().HaveCount(2);
        usages.Should().OnlyContain(u => u.PlanId == "planZ");
        usages[0].PlanStepId.Should().Be("planZ-s0");
        usages[1].PlanStepId.Should().Be("planZ-s1");
        usages[0].InputTokens.Should().Be(111);
        usages[1].InputTokens.Should().Be(333);
    }

    [Fact]
    public async Task Failed_step_short_circuits_remaining_steps_as_skipped()
    {
        var store = new RecordingPlanStore();
        var cost = new RecordingCostTracker();
        var traces = new RecordingTraceCollector();
        PlanExecutor executor = NewExecutor(store, cost, traces);

        var lookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["scorecard"] = MakeSpecialist("scorecard", "ok"),
            ["demand-forecasting"] = MakeSpecialist(
                "demand-forecasting", "unused",
                throwException: new InvalidOperationException("boom")),
            ["competitive-intel"] = MakeSpecialist("competitive-intel", "never-runs"),
        };
        PlanExecutionRequest request = MakeExecutionRequest(lookup,
            ("scorecard", "scorecard", "s"),
            ("demand-forecasting", "demand", "d"),
            ("competitive-intel", "competitive", "c"));

        PlanExecutionOutcome outcome = await executor.ExecuteAsync(request, CancellationToken.None);

        outcome.Status.Should().Be(PlanStatus.Failed);
        outcome.Steps.Should().HaveCount(2); // completed + failed
        outcome.Steps[0].Status.Should().Be(PlanStepStatus.Completed);
        outcome.Steps[1].Status.Should().Be(PlanStepStatus.Failed);
        outcome.FailureReason.Should().NotBeNullOrWhiteSpace();

        PlanStepUpdate[] stepUpdates = [.. store.StepUpdates];
        stepUpdates.Should().Contain(u =>
            u.StepId == "planZ-s2" && u.Status == PlanStepStatus.Skipped);

        PlanStatusUpdate finalPlan = store.StatusUpdates.Last();
        finalPlan.Status.Should().Be(PlanStatus.Failed);
    }

    [Fact]
    public async Task Timed_out_step_is_persisted_as_timed_out_and_terminates_plan()
    {
        var store = new RecordingPlanStore();
        var cost = new RecordingCostTracker();
        var traces = new RecordingTraceCollector();
        var options = new PlanPersistenceOptions
        {
            StepTimeout = TimeSpan.FromMilliseconds(50),
            PlanTimeout = TimeSpan.FromSeconds(10),
            MaxStepCount = 5,
        };
        PlanExecutor executor = NewExecutor(store, cost, traces, options);

        var lookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["slow"] = MakeSpecialist("slow", "never", delay: TimeSpan.FromSeconds(2)),
            ["scorecard"] = MakeSpecialist("scorecard", "never-runs"),
        };
        PlanExecutionRequest request = MakeExecutionRequest(lookup,
            ("slow", "slow", "sleep"),
            ("scorecard", "scorecard", "s"));

        PlanExecutionOutcome outcome = await executor.ExecuteAsync(request, CancellationToken.None);

        outcome.Status.Should().Be(PlanStatus.Failed);
        outcome.Steps.Should().HaveCount(1);
        outcome.Steps[0].Status.Should().Be(PlanStepStatus.TimedOut);

        PlanStepUpdate[] stepUpdates = [.. store.StepUpdates];
        stepUpdates.Should().Contain(u => u.StepId == "planZ-s0" && u.Status == PlanStepStatus.TimedOut);
        stepUpdates.Should().Contain(u => u.StepId == "planZ-s1" && u.Status == PlanStepStatus.Skipped);
    }

    [Fact]
    public async Task Budget_scope_accumulates_across_steps_instead_of_resetting()
    {
        // ADR-006 invariant: RequestToolContext.Begin nested inside an outer
        // plan-scope must be a no-op, so the DistinctCalls counter accumulates
        // across every step's specialist invocation.
        var store = new RecordingPlanStore();
        var cost = new RecordingCostTracker();
        var traces = new RecordingTraceCollector();
        PlanExecutor executor = NewExecutor(store, cost, traces);

        var observed = new ConcurrentBag<RequestToolContext>();
        // Both specialists open their own Begin scope, exactly like the real
        // specialist agents do. If Begin nested inside the plan scope, we
        // should see the SAME RequestToolContext instance in both, and its
        // counters should keep increasing.
        int counter = 0;
        ISpecialistAgent step1 = MakeSpecialist("scorecard", "one", onInvoke: () =>
        {
            using IDisposable inner = RequestToolContext.Begin("user-1");
            RequestToolContext.Current!.Record(
                RequestToolContext.Current.BuildKey("tool.a", "{}"),
                "{}",
                new ToolResultMetrics { ToolName = "tool.a", ReturnedChars = 10 });
            observed.Add(RequestToolContext.Current);
            Interlocked.Increment(ref counter);
        });
        ISpecialistAgent step2 = MakeSpecialist("demand-forecasting", "two", onInvoke: () =>
        {
            using IDisposable inner = RequestToolContext.Begin("user-1");
            RequestToolContext.Current!.Record(
                RequestToolContext.Current.BuildKey("tool.b", "{}"),
                "{}",
                new ToolResultMetrics { ToolName = "tool.b", ReturnedChars = 20 });
            observed.Add(RequestToolContext.Current);
            Interlocked.Increment(ref counter);
        });

        var lookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["scorecard"] = step1,
            ["demand-forecasting"] = step2,
        };
        PlanExecutionRequest request = MakeExecutionRequest(lookup,
            ("scorecard", "scorecard", "s"),
            ("demand-forecasting", "demand", "d"));

        _ = await executor.ExecuteAsync(request, CancellationToken.None);

        counter.Should().Be(2);
        observed.Distinct().Should().HaveCount(1, "both step scopes must reuse the outer plan scope");
        RequestToolContext outerCtx = observed.First();
        outerCtx.DistinctCalls.Should().Be(2);
        outerCtx.CumulativeChars.Should().Be(30);
    }

    [Fact]
    public async Task Unknown_specialist_at_execution_time_is_recorded_as_unusable_step()
    {
        var store = new RecordingPlanStore();
        var cost = new RecordingCostTracker();
        var traces = new RecordingTraceCollector();
        PlanExecutor executor = NewExecutor(store, cost, traces);

        var lookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase);
        PlanExecutionRequest request = MakeExecutionRequest(lookup, ("ghost", "ghost", "x"));

        PlanExecutionOutcome outcome = await executor.ExecuteAsync(request, CancellationToken.None);

        outcome.Steps.Should().HaveCount(1);
        outcome.Steps[0].Status.Should().Be(PlanStepStatus.Unusable);
        outcome.Status.Should().Be(PlanStatus.Unusable);
    }

    [Fact]
    public async Task Plan_outer_scope_reflects_chart_intent_from_original_user_request()
    {
        // ADR-006 chart-intent preservation (#93): when the user request is an
        // explicit chart request, the ONE outer plan budget scope must set
        // IsChartIntent so the tighter chart cap applies to every step of a
        // multi-domain chart request — not just whichever specialist happens
        // to open a scope first, which would silently regress the invariant.
        var store = new RecordingPlanStore();
        var cost = new RecordingCostTracker();
        var traces = new RecordingTraceCollector();
        PlanExecutor executor = NewExecutor(store, cost, traces);

        RequestToolContext? observedChartCtx = null;
        RequestToolContext? observedPlainCtx = null;

        ISpecialistAgent chartSpec = MakeSpecialist("scorecard", "reply-1",
            onInvoke: () =>
            {
                using IDisposable inner = RequestToolContext.Begin("user-1");
                observedChartCtx ??= RequestToolContext.Current;
            });
        ISpecialistAgent plainSpec = MakeSpecialist("demand-forecasting", "reply-2",
            onInvoke: () =>
            {
                using IDisposable inner = RequestToolContext.Begin("user-1");
                observedChartCtx ??= RequestToolContext.Current;
            });

        var lookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["scorecard"] = chartSpec,
            ["demand-forecasting"] = plainSpec,
        };

        // "gauge chart" is an explicit-chart phrase per ChartRequestDetector.
        PlanExecutionRequest chartRequest = MakeExecutionRequest(
            lookup,
            "Show me a gauge chart for portfolio health across every brand.",
            ("scorecard", "scorecard", "s"),
            ("demand-forecasting", "demand", "d"));

        _ = await executor.ExecuteAsync(chartRequest, CancellationToken.None);

        observedChartCtx.Should().NotBeNull().And.Match<RequestToolContext>(c => c.IsChartIntent,
            "the plan's single outer RequestToolContext scope must carry chart intent " +
            "so every step of a multi-domain chart request sees the tighter cap");

        // Sanity: a plain multi-domain request opens a scope WITHOUT chart intent,
        // proving the detector actually runs and the flag isn't stuck at true.
        ISpecialistAgent plainA = MakeSpecialist("scorecard", "reply-a",
            onInvoke: () => observedPlainCtx ??= RequestToolContext.Current);
        ISpecialistAgent plainB = MakeSpecialist("demand-forecasting", "reply-b",
            onInvoke: () => { });

        var lookup2 = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["scorecard"] = plainA,
            ["demand-forecasting"] = plainB,
        };
        PlanExecutionRequest plainRequest = MakeExecutionRequest(
            lookup2,
            "Compare scorecard and demand forecast for our top brands.",
            ("scorecard", "scorecard", "s"),
            ("demand-forecasting", "demand", "d"));

        _ = await executor.ExecuteAsync(plainRequest, CancellationToken.None);

        observedPlainCtx.Should().NotBeNull().And.Match<RequestToolContext>(c => !c.IsChartIntent,
            "plain multi-domain requests must NOT get the tighter chart cap");
    }

    [Fact]
    public async Task Full_width_five_step_plan_executes_every_edge_in_declared_order()
    {
        // ADR-014 pins the plan width at 5. This test proves the workflow
        // graph really wires all five edges (step_i -> step_{i+1}) and each
        // step runs exactly once, in order, when nothing fails.
        var store = new RecordingPlanStore();
        var cost = new RecordingCostTracker();
        var traces = new RecordingTraceCollector();
        PlanExecutor executor = NewExecutor(store, cost, traces);

        var callOrder = new ConcurrentQueue<string>();
        ISpecialistAgent Make(string key, string reply) => MakeSpecialist(
            key, reply, onInvoke: () => callOrder.Enqueue(key));

        var lookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["scorecard"] = Make("scorecard", "score-out"),
            ["demand-forecasting"] = Make("demand-forecasting", "demand-out"),
            ["competitive-intel"] = Make("competitive-intel", "comp-out"),
            ["supply-chain"] = Make("supply-chain", "supply-out"),
            ["margin"] = Make("margin", "margin-out"),
        };

        PlanExecutionRequest request = MakeExecutionRequest(
            lookup,
            ("scorecard", "scorecard", "start"),
            ("demand-forecasting", "demand", "then demand"),
            ("competitive-intel", "competitive", "then competitors"),
            ("supply-chain", "supply", "then supply"),
            ("margin", "margin", "then margin"));

        PlanExecutionOutcome outcome = await executor.ExecuteAsync(request, CancellationToken.None);

        outcome.Status.Should().Be(PlanStatus.Completed);
        outcome.Steps.Should().HaveCount(5);
        outcome.Steps.Select(s => s.SpecialistKey).Should().BeEquivalentTo(
            ["scorecard", "demand-forecasting", "competitive-intel", "supply-chain", "margin"],
            opts => opts.WithStrictOrdering(),
            "every graph edge must fire in declared order without skipping or reordering");
        outcome.Steps.Should().OnlyContain(s => s.Status == PlanStepStatus.Completed);

        callOrder.Should().BeEquivalentTo(
            ["scorecard", "demand-forecasting", "competitive-intel", "supply-chain", "margin"],
            opts => opts.WithStrictOrdering());

        // Persistence honesty: every step has a Completed status update,
        // and the plan finishes with a Completed status update.
        PlanStepUpdate[] stepUpdates = [.. store.StepUpdates];
        for (int i = 0; i < 5; i++)
        {
            stepUpdates.Should().Contain(u =>
                u.StepId == $"planZ-s{i}" && u.Status == PlanStepStatus.Completed);
        }
        store.StatusUpdates.Last().Status.Should().Be(PlanStatus.Completed);
    }

    // ── Cost attribution (issue #300) ────────────────────────────────────
    //
    // These tests pin that plan-step and plan spans emit non-zero cost with
    // an EXACT value derived from the TokenPricing table for the specialist's
    // model. They must fail against the prior behavior (hardcoded 0m) and pass
    // once the executor calls TokenPricing.Calculate.

    private static TokenPricing BuildPricing((string model, decimal inputPerM, decimal outputPerM)[] rows)
    {
        var kv = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (int i = 0; i < rows.Length; i++)
        {
            (string model, decimal inputPerM, decimal outputPerM) = rows[i];
            kv[$"TokenPricing:{model}:InputPerMillion"] =
                inputPerM.ToString(System.Globalization.CultureInfo.InvariantCulture);
            kv[$"TokenPricing:{model}:OutputPerMillion"] =
                outputPerM.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(kv)
            .Build();
        return TokenPricing.FromConfiguration(config);
    }

    [Fact]
    public async Task Plan_step_span_EstimatedCostUsd_matches_TokenPricing_for_configured_model()
    {
        // gpt-priced: $2 per 1M input, $10 per 1M output
        //   input=1000  -> 1000/1_000_000 * 2  = 0.002
        //   output=500  ->  500/1_000_000 * 10 = 0.005
        //   step cost   = 0.007
        var store = new RecordingPlanStore();
        var cost = new RecordingCostTracker();
        var traces = new RecordingTraceCollector();
        TokenPricing pricing = BuildPricing([("gpt-priced", 2.00m, 10.00m)]);
        PlanExecutor executor = NewExecutor(store, cost, traces, pricing: pricing);

        var lookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["scorecard"] = MakeSpecialist(
                "scorecard", "hello",
                inputTokens: 1000, outputTokens: 500,
                model: "gpt-priced"),
        };
        PlanExecutionRequest request = MakeExecutionRequest(lookup,
            ("scorecard", "scorecard", "act"));

        _ = await executor.ExecuteAsync(request, CancellationToken.None);

        TraceSpan stepSpan = traces.Spans.Single(s => s.Tags?["span.type"] == "plan_step");
        stepSpan.EstimatedCostUsd.Should().Be(0.007m,
            "TokenPricing gpt-priced (2/10 per 1M) on 1000/500 tokens = 0.007 USD");
        stepSpan.EstimatedCostUsd.Should().NotBe(0m,
            "regression guard against the pre-#300 hardcoded zero");
    }

    [Fact]
    public async Task Plan_span_EstimatedCostUsd_equals_sum_of_per_step_costs()
    {
        // step 1 (fast model, $1/$5): input=1000, output=500
        //   = 0.001 + 0.0025 = 0.0035
        // step 2 (heavy model, $5/$25): input=200, output=800
        //   = 0.001 + 0.020  = 0.021
        // plan cost = 0.0245
        var store = new RecordingPlanStore();
        var cost = new RecordingCostTracker();
        var traces = new RecordingTraceCollector();
        TokenPricing pricing = BuildPricing(
        [
            ("fast",  1.00m, 5.00m),
            ("heavy", 5.00m, 25.00m),
        ]);
        PlanExecutor executor = NewExecutor(store, cost, traces, pricing: pricing);

        var lookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["scorecard"] = MakeSpecialist(
                "scorecard", "a",
                inputTokens: 1000, outputTokens: 500, model: "fast"),
            ["demand-forecasting"] = MakeSpecialist(
                "demand-forecasting", "b",
                inputTokens: 200, outputTokens: 800, model: "heavy"),
        };
        PlanExecutionRequest request = MakeExecutionRequest(lookup,
            ("scorecard", "scorecard", "s"),
            ("demand-forecasting", "demand", "d"));

        _ = await executor.ExecuteAsync(request, CancellationToken.None);

        TraceSpan[] stepSpans = [.. traces.Spans.Where(s => s.Tags?["span.type"] == "plan_step")];
        stepSpans.Should().HaveCount(2);
        stepSpans[0].EstimatedCostUsd.Should().Be(0.0035m);
        stepSpans[1].EstimatedCostUsd.Should().Be(0.021m);

        TraceSpan planSpan = traces.Spans.Single(s => s.Tags?["span.type"] == "plan");
        planSpan.EstimatedCostUsd.Should().Be(0.0245m,
            "plan-level cost is the sum of every step's TokenPricing cost");
    }

    [Fact]
    public async Task Plan_step_span_EstimatedCostUsd_is_zero_when_zero_tokens_reported()
    {
        // Zero-token step (e.g. a specialist that returns no TokenUsage). Even
        // with a configured pricing row the calculation must be 0 — matches the
        // TokenPricing contract (cache hit / zero-usage short-circuit).
        var store = new RecordingPlanStore();
        var cost = new RecordingCostTracker();
        var traces = new RecordingTraceCollector();
        TokenPricing pricing = BuildPricing([("gpt-priced", 2.00m, 10.00m)]);
        PlanExecutor executor = NewExecutor(store, cost, traces, pricing: pricing);

        var lookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["scorecard"] = MakeSpecialist(
                "scorecard", "hello",
                inputTokens: 0, outputTokens: 0,
                model: "gpt-priced"),
        };
        PlanExecutionRequest request = MakeExecutionRequest(lookup,
            ("scorecard", "scorecard", "act"));

        _ = await executor.ExecuteAsync(request, CancellationToken.None);

        TraceSpan stepSpan = traces.Spans.Single(s => s.Tags?["span.type"] == "plan_step");
        stepSpan.EstimatedCostUsd.Should().Be(0m);

        TraceSpan planSpan = traces.Spans.Single(s => s.Tags?["span.type"] == "plan");
        planSpan.EstimatedCostUsd.Should().Be(0m);
    }

    [Fact]
    public async Task Plan_step_span_EstimatedCostUsd_uses_TokenPricing_default_for_unknown_model()
    {
        // Unknown-model behavior: TokenPricing.Calculate falls back to its
        // built-in default of ($1 input, $5 output per 1M). This test pins
        // that the executor invokes Calculate (rather than short-circuiting
        // to zero) so the sanctioned pricing policy — including its default —
        // reaches the span.
        //
        //   input=1000  ->  0.001
        //   output=500  ->  0.0025
        //   total       =  0.0035
        var store = new RecordingPlanStore();
        var cost = new RecordingCostTracker();
        var traces = new RecordingTraceCollector();
        // NB: no row for "no-price-here" — Calculate uses its default table.
        TokenPricing pricing = BuildPricing([("some-other-model", 99m, 99m)]);
        PlanExecutor executor = NewExecutor(store, cost, traces, pricing: pricing);

        var lookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["scorecard"] = MakeSpecialist(
                "scorecard", "hello",
                inputTokens: 1000, outputTokens: 500,
                model: "no-price-here"),
        };
        PlanExecutionRequest request = MakeExecutionRequest(lookup,
            ("scorecard", "scorecard", "act"));

        _ = await executor.ExecuteAsync(request, CancellationToken.None);

        TraceSpan stepSpan = traces.Spans.Single(s => s.Tags?["span.type"] == "plan_step");
        stepSpan.EstimatedCostUsd.Should().Be(0.0035m,
            "TokenPricing default (1/5 per 1M) applies when the model is not in the pricing table");
    }
}

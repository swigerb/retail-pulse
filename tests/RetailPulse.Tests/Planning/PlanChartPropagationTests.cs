using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Api.Agents.Planning;
using RetailPulse.Api.Persistence;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Persistence;
using RetailPulse.Contracts.Routing;
using RetailPulse.Contracts.Tracing;

namespace RetailPulse.Tests.Planning;

/// <summary>
/// Wave 2 QA sweep (#97) — Defect B: prior to this PR, every ChartSpec a
/// specialist emitted on the plan path was silently discarded because
/// <see cref="PlanStepResult"/> did not carry Charts, so
/// <see cref="PlanOrchestrationResult"/> could not surface them to
/// <c>/api/chat</c>. That made ADR-006's "9 chart types on both paths"
/// acceptance impossible to enforce. This suite locks in the fix at two
/// layers: (a) each of the 9 canonical chart types is preserved on
/// <see cref="PlanStepResult.Charts"/> after execution, and (b) the
/// aggregate <see cref="PlanOrchestrationResult.Charts"/> flattens
/// multi-step charts in specialist order.
/// </summary>
public sealed class PlanChartPropagationTests
{
    /// <summary>
    /// Canonical chart types per <c>ChartSpecValidator._knownChartTypes</c>.
    /// Every entry MUST round-trip through the plan executor unchanged.
    /// </summary>
    public static IEnumerable<object[]> KnownChartTypes =>
    [
        ["line"], ["bar"], ["groupedBar"], ["stackedBar"], ["horizontalBar"],
        ["pie"], ["donut"], ["gauge"], ["table"],
    ];

    [Theory]
    [MemberData(nameof(KnownChartTypes))]
    public async Task PlanExecutor_preserves_specialist_ChartSpec_on_step_result(string chartType)
    {
        var store = new NoopPlanStore();
        var cost = new NullCostTracker();
        var traces = new NullTraceCollector();
        var executor = new PlanExecutor(
            store, cost, traces,
            new PlanPersistenceOptions(),
            NullLogger<PlanExecutor>.Instance);

        ChartSpec chart = new()
        {
            Type = chartType,
            Title = $"{chartType} exemplar",
            Data =
            [
                new ChartSeries
                {
                    Legend = "series-a",
                    Values = [new() { X = "Q1", Y = 1 }, new() { X = "Q2", Y = 2 }],
                },
            ],
        };

        var lookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["demand-forecasting"] = StubSpecialistReturning("demand-forecasting", "reply", [chart]),
        };

        PlanExecutionRequest req = MakeRequest(lookup, "chart me",
            ("demand-forecasting", "chart", "produce"));

        PlanExecutionOutcome outcome = await executor.ExecuteAsync(req, CancellationToken.None);

        outcome.Status.Should().Be(PlanStatus.Completed);
        outcome.Steps.Should().HaveCount(1);
        outcome.Steps[0].Charts.Should().NotBeNull("PlanStepResult must carry Charts across the executor boundary.");
        outcome.Steps[0].Charts!.Should().HaveCount(1);
        outcome.Steps[0].Charts![0].Type.Should().Be(chartType,
            "chart type must survive the executor without transformation.");
    }

    [Fact]
    public async Task PlanOrchestrationResult_Charts_flattens_multi_step_specialist_output_in_order()
    {
        // Multi-domain composition: two specialists each contribute a chart.
        // The aggregate MUST preserve specialist order so the frontend can
        // annotate charts back to their originating step.
        var store = new NoopPlanStore();
        var cost = new NullCostTracker();
        var traces = new NullTraceCollector();
        var executor = new PlanExecutor(
            store, cost, traces,
            new PlanPersistenceOptions(),
            NullLogger<PlanExecutor>.Instance);

        ChartSpec chartA = new() { Type = "line", Title = "A", Data = [MakeSeries("s")] };
        ChartSpec chartB = new() { Type = "gauge", Title = "B", Data = [MakeSeries("s")] };

        var lookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["demand-forecasting"] = StubSpecialistReturning("demand-forecasting", "a", [chartA]),
            ["supply-shipments"] = StubSpecialistReturning("supply-shipments", "b", [chartB]),
        };

        PlanExecutionRequest req = MakeRequest(lookup, "compare demand + inventory",
            ("demand-forecasting", "chart", "produce"),
            ("supply-shipments", "chart", "produce"));

        PlanExecutionOutcome outcome = await executor.ExecuteAsync(req, CancellationToken.None);
        outcome.Status.Should().Be(PlanStatus.Completed);

        // Simulate the orchestrator step of the pipeline: the endpoint uses
        // PlanOrchestrationResult.Charts to hand the frontend a flattened
        // list. We construct one here from the outcome so the test pins
        // ordering without dragging the full orchestrator into scope.
        var aggregate = new PlanOrchestrationResult(
            PlanId: req.PlanId,
            Status: outcome.Status,
            Reply: "final",
            DurationMs: 0,
            InputTokens: 0, OutputTokens: 0, TotalTokens: 0,
            Steps: outcome.Steps,
            FailureReason: null);

        aggregate.Charts.Should().HaveCount(2, "both specialist charts must appear on the plan-path response.");
        aggregate.Charts[0].Type.Should().Be("line");
        aggregate.Charts[1].Type.Should().Be("gauge");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static ChartSeries MakeSeries(string legend) => new()
    {
        Legend = legend,
        Values = [new() { X = "x", Y = 1 }],
    };

    private static ISpecialistAgent StubSpecialistReturning(
        string key, string reply, List<ChartSpec>? charts)
    {
        var mock = new Mock<ISpecialistAgent>();
        mock.SetupGet(a => a.Key).Returns(key);
        mock.SetupGet(a => a.DisplayName).Returns(key);
        mock.SetupGet(a => a.Model).Returns("gpt-test");
        mock.SetupGet(a => a.SupportedIntents).Returns([key]);
        mock.Setup(a => a.HandleAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(
                reply,
                "session-x",
                [],
                charts,
                10,
                new TokenUsage(10, 5, 15)));
        return mock.Object;
    }

    private static PlanExecutionRequest MakeRequest(
        Dictionary<string, ISpecialistAgent> lookup,
        string message,
        params (string key, string intent, string action)[] steps)
    {
        var planned = steps.Select(s => new PlannerStep
        {
            SpecialistKey = s.key,
            Intent = s.intent,
            Action = s.action,
        }).ToList();
        return new PlanExecutionRequest
        {
            PlanId = "plan-chart",
            Subject = "user-1",
            PrincipalKey = "user-1",
            SessionId = "session-x",
            TraceId = "trace-x",
            ParentSpanId = null,
            Request = message,
            History = null,
            User = new UserContext("user-1", "User One", string.Empty),
            Plan = new PlanBuildResult { Steps = planned },
            StepIds = [.. planned.Select((_, i) => $"plan-chart-s{i}")],
            SpecialistLookup = lookup,
        };
    }

    private sealed class NoopPlanStore : IPlanStore
    {
        public Task CreatePlanAsync(PlanWrite plan, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdatePlanStatusAsync(PlanStatusUpdate update, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateStepAsync(PlanStepUpdate update, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<PlanSummaryDto>> ListPlansForSubjectAsync(string subject, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PlanSummaryDto>>([]);
        public Task<PlanDetailDto?> GetPlanAsync(string subject, string planId, CancellationToken ct = default) => Task.FromResult<PlanDetailDto?>(null);
        public Task<bool> DeletePlanAsync(string subject, string planId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<PlanCleanupResult> PurgeExpiredAsync(DateTimeOffset olderThan, CancellationToken ct = default) => Task.FromResult(new PlanCleanupResult(0, 0));
    }

    private sealed class NullCostTracker : Contracts.Observability.ICostTracker
    {
        public Task TrackUsageAsync(Contracts.Observability.UsageEvent usage, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Contracts.Observability.CostSummary> GetSummaryAsync(Contracts.Observability.CostPeriod period, CancellationToken ct = default)
            => Task.FromResult(new Contracts.Observability.CostSummary(0, 0, 0, period));
        public Task<IReadOnlyList<Contracts.Observability.AgentCostBreakdown>> GetByAgentAsync(Contracts.Observability.CostPeriod period, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Contracts.Observability.AgentCostBreakdown>>([]);
        public Task<Contracts.Observability.CostTrend> GetTrendAsync(int days = 7, CancellationToken ct = default)
            => Task.FromResult(new Contracts.Observability.CostTrend([]));
    }

    private sealed class NullTraceCollector : ITraceCollector
    {
        public void CaptureSpan(TraceSpan span) { }
        public IReadOnlyList<TraceSpan>? GetSpans(string traceId) => null;
        public TraceSummary? GetSummary(string traceId) => null;
        public IReadOnlyList<TraceSummary> GetRecentTraces(int count = 20) => [];
        public StructuredTraceSummary? GetStructuredSummary(string traceId) => null;
        public IReadOnlyList<ToolUsageStat> GetToolStats(DateTimeOffset since, int top = 10) => [];
        public int TraceCount => 0;
        public int Capacity => 0;
    }
}

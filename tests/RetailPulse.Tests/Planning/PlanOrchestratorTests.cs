using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Agents.Planning;
using RetailPulse.Api.Persistence;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Observability;
using RetailPulse.Contracts.Persistence;
using RetailPulse.Contracts.Routing;
using RetailPulse.Contracts.Tracing;
using RetailPulse.Tests.Fixtures;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Tests.Planning;

/// <summary>
/// Integration-style tests for the top-level composition — plan build,
/// persist, execute, compose reply — with a stubbed IChatClient so we can
/// pin the honest terminal states #93 requires.
/// </summary>
public sealed class PlanOrchestratorTests
{
    private sealed class RecordingPlanStore : IPlanStore
    {
        public ConcurrentQueue<PlanWrite> Creates { get; } = new();
        public ConcurrentQueue<PlanStatusUpdate> StatusUpdates { get; } = new();
        public ConcurrentQueue<PlanStepUpdate> StepUpdates { get; } = new();

        public Task CreatePlanAsync(PlanWrite plan, CancellationToken ct = default)
        { Creates.Enqueue(plan); return Task.CompletedTask; }
        public Task UpdatePlanStatusAsync(PlanStatusUpdate update, CancellationToken ct = default)
        { StatusUpdates.Enqueue(update); return Task.CompletedTask; }
        public Task<bool> TryTransitionStatusAsync(string planId, string subject, string fromStatus, string toStatus, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task UpdateStepAsync(PlanStepUpdate update, CancellationToken ct = default)
        { StepUpdates.Enqueue(update); return Task.CompletedTask; }
        public Task ReplacePlanStepsFromIndexAsync(string planId, string subject, int fromStepIndex, IReadOnlyList<PlanStepWrite> steps, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<IReadOnlyList<PlanSummaryDto>> ListPlansForSubjectAsync(string subject, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PlanSummaryDto>>([]);
        public Task<PlanDetailDto?> GetPlanAsync(string subject, string planId, CancellationToken ct = default)
            => Task.FromResult<PlanDetailDto?>(null);
        public Task<bool> DeletePlanAsync(string subject, string planId, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<PlanCleanupResult> PurgeExpiredAsync(DateTimeOffset olderThan, CancellationToken ct = default)
            => Task.FromResult(new PlanCleanupResult(0, 0));
    }

    private sealed class NoOpTraceCollector : ITraceCollector
    {
        public void CaptureSpan(TraceSpan span) { }
        public IReadOnlyList<TraceSpan>? GetSpans(string traceId) => null;
        public TraceSummary? GetSummary(string traceId) => null;
        public IReadOnlyList<TraceSummary> GetRecentTraces(int count = 20) => [];
        public StructuredTraceSummary? GetStructuredSummary(string traceId) => null;
        public IReadOnlyList<ToolUsageStat> GetToolStats(DateTimeOffset since, int top = 10) => [];
        public int TraceCount => 0;
        public int Capacity => 100;
    }

    private sealed class NoOpCostTracker : ICostTracker
    {
        public Task TrackUsageAsync(UsageEvent usage, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CostSummary> GetSummaryAsync(CostPeriod period, CancellationToken ct = default)
            => Task.FromResult(new CostSummary(0, 0, 0, period));
        public Task<IReadOnlyList<AgentCostBreakdown>> GetByAgentAsync(CostPeriod period, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentCostBreakdown>>([]);
        public Task<CostTrend> GetTrendAsync(int days = 7, CancellationToken ct = default)
            => Task.FromResult(new CostTrend([]));
    }

    /// <summary>
    /// Cost tracker that captures every <see cref="UsageEvent"/> so tests can
    /// assert plan-level (PlanStepId = null) vs step-level attribution.
    /// </summary>
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

    /// <summary>
    /// Mock IChatClient that returns fixed text AND a caller-supplied
    /// <see cref="UsageDetails"/> so tests can pin planner token attribution.
    /// </summary>
    private static IChatClient CreateMockChatClientWithUsage(string responseText, int inputTokens, int outputTokens)
    {
        var mock = new Moq.Mock<IChatClient>();
        var response = new Microsoft.Extensions.AI.ChatResponse(
            new ChatMessage(ChatRole.Assistant, responseText))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
                TotalTokenCount = inputTokens + outputTokens,
            },
        };
        mock
            .Setup(x => x.GetResponseAsync(
                Moq.It.IsAny<IEnumerable<ChatMessage>>(),
                Moq.It.IsAny<ChatOptions>(),
                Moq.It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(response));
        return mock.Object;
    }

    private static Api.Models.AgentDefinition PlannerDef() => new()
    {
        Key = "planner",
        Name = "Plan-First Orchestrator",
        Model = "gpt-5.4-mini",
        SystemPrompt = "You are the planner.",
        Temperature = 0.1,
    };

    [Fact]
    public async Task Unusable_planner_output_persists_unusable_plan_and_invokes_no_specialist()
    {
        // The planner replies with an empty steps array plus a reason.
        // Expected: exactly ONE Create call with status Unusable, ONE
        // StatusUpdate with status Unusable, and zero specialist invocations.
        var store = new RecordingPlanStore();
        IChatClient chatClient = AgentTestFixtures.CreateMockChatClient(
            /*lang=json,strict*/ @"{ ""steps"": [], ""reason"": ""single-domain question — planner declined"" }");

        var options = new PlanPersistenceOptions();
        var builder = new PlanBuilder(chatClient, PlannerDef(), options, NullLogger<PlanBuilder>.Instance);
        var cost = new RecordingCostTracker();
        var executor = new PlanExecutor(store, cost, new NoOpTraceCollector(), options,
            NullLogger<PlanExecutor>.Instance);
        var orchestrator = new PlanOrchestrator(builder, executor, store, cost, options,
            NullLogger<PlanOrchestrator>.Instance);

        // A specialist that must NOT be invoked.
        int invocations = 0;
        var scorecard = new Moq.Mock<ISpecialistAgent>();
        scorecard.SetupGet(a => a.Key).Returns("scorecard");
        scorecard.SetupGet(a => a.DisplayName).Returns("Scorecard");
        scorecard.SetupGet(a => a.Model).Returns("gpt-test");
        scorecard.SetupGet(a => a.SupportedIntents).Returns(["scorecard"]);
        scorecard
            .Setup(a => a.HandleAsync(Moq.It.IsAny<ChatRequest>(), Moq.It.IsAny<CancellationToken>()))
            .Returns((ChatRequest _, CancellationToken __) =>
            {
                Interlocked.Increment(ref invocations);
                return Task.FromResult(new ChatResponse("nope", "s", []));
            });
        var lookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["scorecard"] = scorecard.Object,
        };

        PlanOrchestrationResult result = await orchestrator.RunAsync(new PlanOrchestrationInput
        {
            Request = new ChatRequest("How am I?", SessionId: "s"),
            Subject = "user-1",
            PrincipalKey = "user-1",
            TenantId = "Contoso",
            Roster = [scorecard.Object],
            SpecialistLookup = lookup,
            DetectedIntents = ["scorecard"],
            TraceId = "t",
        }, CancellationToken.None);

        result.Status.Should().Be(PlanStatus.Unusable);
        result.Steps.Should().BeEmpty();
        result.FailureReason.Should().NotBeNullOrWhiteSpace();
        invocations.Should().Be(0, "an unusable plan must not touch any specialist");

        store.Creates.Should().HaveCount(1);
        store.Creates.Single().Status.Should().Be(PlanStatus.Unusable);
        store.StatusUpdates.Should().HaveCount(1);
        store.StatusUpdates.Single().Status.Should().Be(PlanStatus.Unusable);
    }

    [Fact]
    public async Task Usable_plan_persists_running_row_then_terminal_row_and_composes_reply()
    {
        var store = new RecordingPlanStore();
        IChatClient chatClient = AgentTestFixtures.CreateMockChatClient(
            /*lang=json,strict*/ @"{ ""steps"": [
                { ""specialist_key"": ""scorecard"", ""intent"": ""scorecard"", ""action"": ""summarize"" },
                { ""specialist_key"": ""demand-forecasting"", ""intent"": ""demand"", ""action"": ""forecast"" }
            ] }");

        var options = new PlanPersistenceOptions();
        var builder = new PlanBuilder(chatClient, PlannerDef(), options, NullLogger<PlanBuilder>.Instance);
        var cost = new RecordingCostTracker();
        var executor = new PlanExecutor(store, cost, new NoOpTraceCollector(), options,
            NullLogger<PlanExecutor>.Instance);
        var orchestrator = new PlanOrchestrator(builder, executor, store, cost, options,
            NullLogger<PlanOrchestrator>.Instance);

        static ISpecialistAgent MakeSpec(string key, string reply)
        {
            var m = new Moq.Mock<ISpecialistAgent>();
            m.SetupGet(a => a.Key).Returns(key);
            m.SetupGet(a => a.DisplayName).Returns(key);
            m.SetupGet(a => a.Model).Returns("gpt-test");
            m.SetupGet(a => a.SupportedIntents).Returns([key]);
            m.Setup(a => a.HandleAsync(Moq.It.IsAny<ChatRequest>(), Moq.It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(new ChatResponse(reply, "s", [], null, 10,
                    new TokenUsage(50, 20, 70))));
            return m.Object;
        }

        ISpecialistAgent s1 = MakeSpec("scorecard", "SCORECARD SAYS: green");
        ISpecialistAgent s2 = MakeSpec("demand-forecasting", "DEMAND SAYS: rising");
        var lookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["scorecard"] = s1,
            ["demand-forecasting"] = s2,
        };

        PlanOrchestrationResult result = await orchestrator.RunAsync(new PlanOrchestrationInput
        {
            Request = new ChatRequest("multi domain", SessionId: "s"),
            Subject = "user-1",
            PrincipalKey = "user-1",
            TenantId = "Contoso",
            Roster = [s1, s2],
            SpecialistLookup = lookup,
            DetectedIntents = ["scorecard", "demand"],
            TraceId = "t",
        }, CancellationToken.None);

        result.Status.Should().Be(PlanStatus.Completed);
        result.Reply.Should().Contain("SCORECARD");
        result.Reply.Should().Contain("DEMAND");
        result.Steps.Should().HaveCount(2);

        // One create (running) and one status update (completed).
        store.Creates.Single().Status.Should().Be(PlanStatus.Running);
        store.StatusUpdates.Last().Status.Should().Be(PlanStatus.Completed);
    }

    [Fact]
    public async Task Planner_usage_is_attributed_to_plan_level_event_and_steps_still_attributed_individually()
    {
        // #93 quality gate: the planner LLM call must produce its own UsageEvent
        // with PlanId populated and PlanStepId = null (plan-level attribution),
        // in addition to the per-step UsageEvents the executor emits for each
        // specialist. Rolling planner tokens silently into the plan aggregate
        // would hide the planner's cost from audits; double-charging them
        // against a step would inflate that step's attribution. This test
        // pins the exact division: one planner event + one event per step,
        // all sharing the same PlanId.
        var store = new RecordingPlanStore();
        var cost = new RecordingCostTracker();

        // Planner returns a two-step usable plan AND reports non-zero usage.
        IChatClient chatClient = CreateMockChatClientWithUsage(
            /*lang=json,strict*/ @"{ ""steps"": [
                { ""specialist_key"": ""scorecard"", ""intent"": ""scorecard"", ""action"": ""summarize"" },
                { ""specialist_key"": ""demand-forecasting"", ""intent"": ""demand"", ""action"": ""forecast"" }
            ] }",
            inputTokens: 137,
            outputTokens: 42);

        var options = new PlanPersistenceOptions();
        var builder = new PlanBuilder(chatClient, PlannerDef(), options, NullLogger<PlanBuilder>.Instance);
        var executor = new PlanExecutor(store, cost, new NoOpTraceCollector(), options,
            NullLogger<PlanExecutor>.Instance);
        var orchestrator = new PlanOrchestrator(builder, executor, store, cost, options,
            NullLogger<PlanOrchestrator>.Instance);

        static ISpecialistAgent MakeSpec(string key, string reply, int input, int output)
        {
            var m = new Moq.Mock<ISpecialistAgent>();
            m.SetupGet(a => a.Key).Returns(key);
            m.SetupGet(a => a.DisplayName).Returns(key);
            m.SetupGet(a => a.Model).Returns("gpt-specialist");
            m.SetupGet(a => a.SupportedIntents).Returns([key]);
            m.Setup(a => a.HandleAsync(Moq.It.IsAny<ChatRequest>(), Moq.It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(new ChatResponse(reply, "s", [], null, 10,
                    new TokenUsage(input, output, input + output))));
            return m.Object;
        }

        ISpecialistAgent s1 = MakeSpec("scorecard", "score-reply", 200, 60);
        ISpecialistAgent s2 = MakeSpec("demand-forecasting", "demand-reply", 300, 80);
        var lookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["scorecard"] = s1,
            ["demand-forecasting"] = s2,
        };

        PlanOrchestrationResult result = await orchestrator.RunAsync(new PlanOrchestrationInput
        {
            Request = new ChatRequest("multi domain", SessionId: "s"),
            Subject = "user-1",
            PrincipalKey = "user-1",
            TenantId = "Contoso",
            Roster = [s1, s2],
            SpecialistLookup = lookup,
            DetectedIntents = ["scorecard", "demand"],
            TraceId = "t",
        }, CancellationToken.None);

        result.Status.Should().Be(PlanStatus.Completed);

        UsageEvent[] usages = [.. cost.Usages];
        usages.Should().HaveCount(3, "one planner + two specialists");

        // Every event shares the same PlanId — the runtime one we minted.
        string planId = result.PlanId;
        usages.Should().OnlyContain(u => u.PlanId == planId);

        // Plan-level event: PlanStepId is null, AgentId is 'planner', tokens
        // match what the planner LLM reported and the model comes from the
        // planner AgentDefinition (proves the flow is clean, not fabricated).
        UsageEvent plannerEvent = usages.Single(u => u.PlanStepId is null);
        plannerEvent.AgentId.Should().Be("planner");
        plannerEvent.Model.Should().Be("gpt-5.4-mini");
        plannerEvent.InputTokens.Should().Be(137);
        plannerEvent.OutputTokens.Should().Be(42);

        // Step-level events preserve per-specialist attribution — the planner
        // call must NOT be silently rolled onto any specialist step.
        UsageEvent[] stepEvents = [.. usages.Where(u => u.PlanStepId is not null)];
        stepEvents.Should().HaveCount(2);
        stepEvents.Should().Contain(u => u.AgentId == "scorecard" && u.InputTokens == 200 && u.OutputTokens == 60);
        stepEvents.Should().Contain(u => u.AgentId == "demand-forecasting" && u.InputTokens == 300 && u.OutputTokens == 80);

        // Guardrail: no specialist step is double-charged for planner tokens.
        stepEvents.Should().NotContain(u => u.InputTokens == 137 || u.OutputTokens == 42);
    }
}

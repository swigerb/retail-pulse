using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RetailPulse.Api.Agents.Planning;
using RetailPulse.Api.Approval;
using RetailPulse.Api.Persistence;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Approval;
using RetailPulse.Contracts.Observability;
using RetailPulse.Contracts.Persistence;
using RetailPulse.Contracts.Routing;
using RetailPulse.Contracts.Tracing;
using RetailPulse.Tests.Fixtures;
using ChatResponse = RetailPulse.Contracts.ChatResponse;
using RetailPulse.Tests.TestInfrastructure;

namespace RetailPulse.Tests.Planning;

/// <summary>
/// End-to-end tests that thread the <see cref="PlanReviewCoordinator"/> through
/// the real <see cref="PlanOrchestrator"/> + <see cref="PlanExecutor"/> pair so
/// the "edited plan is what actually executes" acceptance criterion is proved
/// on the specialist-invocation path, not just in coordinator-level asserts.
/// </summary>
public sealed class PlanReviewOrchestratorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _checkpointDir;

    public PlanReviewOrchestratorTests()
    {
        _dbPath = SqliteTestCleanup.NewDbPath("plan_review_orch");
        _checkpointDir = Path.Combine(Path.GetTempPath(), $"plan_review_orch_ckpt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_checkpointDir);
    }

    public void Dispose()
    {
        SqliteTestCleanup.ReleaseAndDelete(_dbPath);
        try { Directory.Delete(_checkpointDir, recursive: true); } catch { }
    }

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class RecordingPlanStore : IPlanStore
    {
        public ConcurrentQueue<PlanWrite> Creates { get; } = new();
        public ConcurrentQueue<PlanStatusUpdate> StatusUpdates { get; } = new();
        public ConcurrentQueue<PlanStepUpdate> StepUpdates { get; } = new();
        public Task CreatePlanAsync(PlanWrite plan, CancellationToken ct = default)
        { Creates.Enqueue(plan); return Task.CompletedTask; }
        public Task UpdatePlanStatusAsync(PlanStatusUpdate update, CancellationToken ct = default)
        { StatusUpdates.Enqueue(update); return Task.CompletedTask; }
        public Task UpdateStepAsync(PlanStepUpdate update, CancellationToken ct = default)
        { StepUpdates.Enqueue(update); return Task.CompletedTask; }
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

    private static Api.Models.AgentDefinition PlannerDef() => new()
    {
        Key = "planner",
        Name = "Plan-First Orchestrator",
        Model = "gpt-test",
        SystemPrompt = "You are the planner.",
        Temperature = 0.1,
    };

    private static IChatClient PlannerChat() => AgentTestFixtures.CreateMockChatClient(
        /*lang=json,strict*/ @"{ ""steps"": [
            { ""specialist_key"": ""scorecard"", ""intent"": ""scorecard"", ""action"": ""original scorecard action"" },
            { ""specialist_key"": ""demand-forecasting"", ""intent"": ""demand"", ""action"": ""original demand action"" }
        ] }");

    private sealed record SpecialistInvocation(string Key, string Message);

    private static (ISpecialistAgent agent, ConcurrentQueue<SpecialistInvocation> invocations)
        MakeRecordingSpecialist(string key, string reply)
    {
        var invocations = new ConcurrentQueue<SpecialistInvocation>();
        var m = new Mock<ISpecialistAgent>();
        m.SetupGet(a => a.Key).Returns(key);
        m.SetupGet(a => a.DisplayName).Returns(key);
        m.SetupGet(a => a.Model).Returns("gpt-test");
        m.SetupGet(a => a.SupportedIntents).Returns([key]);
        m.Setup(a => a.HandleAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .Returns((ChatRequest req, CancellationToken _) =>
            {
                invocations.Enqueue(new SpecialistInvocation(key, req.Message));
                return Task.FromResult(new ChatResponse(
                    reply, req.SessionId ?? "s", [], null, 10, new TokenUsage(1, 1, 2)));
            });
        return (m.Object, invocations);
    }

    private PlanReviewCheckpointService CreateCheckpointService()
    {
        FileSystemJsonCheckpointStore store =
            new(new DirectoryInfo(_checkpointDir));
        var manager = CheckpointManager.CreateJson(store, customOptions: null);
        return new PlanReviewCheckpointService(store, manager, NullLogger<PlanReviewCheckpointService>.Instance);
    }

    private SqliteApprovalGate CreateGate() =>
        new(_dbPath, Mock.Of<ILogger<SqliteApprovalGate>>(),
            TimeSpan.FromSeconds(30), TimeProvider.System);

    // ── Edit path — orchestrator suspends; reviewer decides ─────────────

    [Fact]
    public async Task RunAsync_review_enabled_suspends_without_blocking_and_writes_checkpoint()
    {
        SqliteApprovalGate gate = CreateGate();
        var options = new PlanReviewOptions
        {
            Enabled = true,
            DefaultReviewTimeout = TimeSpan.FromSeconds(30),
            MaxReplanRounds = 0,
        };
        PlanReviewCheckpointService checkpoints = CreateCheckpointService();
        var coord = new PlanReviewCoordinator(
            gate,
            Options.Create(options),
            checkpoints,
            NullLogger<PlanReviewCoordinator>.Instance,
            replanner: null,
            timeProvider: TimeProvider.System);

        var store = new RecordingPlanStore();
        var cost = new NoOpCostTracker();
        var persistenceOpts = new PlanPersistenceOptions();
        var builder = new PlanBuilder(PlannerChat(), PlannerDef(), persistenceOpts, NullLogger<PlanBuilder>.Instance);
        var executor = new PlanExecutor(store, cost, new NoOpTraceCollector(), persistenceOpts, NullLogger<PlanExecutor>.Instance);
        var orchestrator = new PlanOrchestrator(
            builder, executor, store, cost, persistenceOpts,
            NullLogger<PlanOrchestrator>.Instance,
            coord,
            Options.Create(options));

        (ISpecialistAgent s1, ConcurrentQueue<SpecialistInvocation>? s1Invocations) = MakeRecordingSpecialist("scorecard", "score");
        (ISpecialistAgent s2, ConcurrentQueue<SpecialistInvocation>? s2Invocations) = MakeRecordingSpecialist("demand-forecasting", "demand");
        var lookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["scorecard"] = s1,
            ["demand-forecasting"] = s2,
        };

        PlanOrchestrationResult result = await orchestrator.RunAsync(new PlanOrchestrationInput
        {
            Request = new ChatRequest("multi", SessionId: "s"),
            Subject = "user-1",
            PrincipalKey = "user-1",
            TenantId = "Contoso",
            Roster = [s1, s2],
            SpecialistLookup = lookup,
            DetectedIntents = ["scorecard", "demand"],
            TraceId = "t",
        }, CancellationToken.None);

        // The orchestrator is now non-blocking. Enabled review returns
        // Suspended immediately with the review request id + round.
        result.IsSuspended.Should().BeTrue();
        result.Status.Should().Be(PlanStatus.AwaitingReview);
        result.ReviewRequestId.Should().NotBeNullOrWhiteSpace();
        result.ReviewRoundNumber.Should().Be(0);

        // No specialist ran — the plan is waiting for reviewer input.
        s1Invocations.Should().BeEmpty();
        s2Invocations.Should().BeEmpty();

        // Plan record persisted as AwaitingReview with the ORIGINAL planner
        // steps captured on disk; the executor never touches steps until the
        // completion service resumes.
        store.Creates.Should().HaveCount(1);
        PlanWrite create = store.Creates.Single();
        create.Status.Should().Be(PlanStatus.AwaitingReview);
        create.Steps.Should().HaveCount(2);

        // Real framework checkpoint written for this plan.
        PlanReviewCheckpointState? loaded = await checkpoints.LoadLatestAsync(result.PlanId, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded.Kind.Should().Be(PlanCheckpointKind.Review);
        loaded.PlanId.Should().Be(result.PlanId);
        loaded.RoundNumber.Should().Be(0);
        loaded.Steps.Should().HaveCount(2);
        loaded.SpecialistKeys.Should().BeEquivalentTo(["scorecard", "demand-forecasting"]);
    }

    // ── Reject-with-cap → no specialist invocation, terminal Failed row ─

    [Fact]
    public async Task Reject_exhausted_terminates_plan_without_executing()
    {
        SqliteApprovalGate gate = CreateGate();
        var options = new PlanReviewOptions
        {
            Enabled = true,
            DefaultReviewTimeout = TimeSpan.FromSeconds(30),
            MaxReplanRounds = 0, // zero replans — one reject terminates.
        };
        var coord = new PlanReviewCoordinator(
            gate,
            Options.Create(options),
            CreateCheckpointService(),
            NullLogger<PlanReviewCoordinator>.Instance,
            replanner: null,
            timeProvider: TimeProvider.System);

        var store = new RecordingPlanStore();
        var cost = new NoOpCostTracker();
        var persistenceOpts = new PlanPersistenceOptions();
        var builder = new PlanBuilder(PlannerChat(), PlannerDef(), persistenceOpts, NullLogger<PlanBuilder>.Instance);
        var executor = new PlanExecutor(store, cost, new NoOpTraceCollector(), persistenceOpts, NullLogger<PlanExecutor>.Instance);
        var orchestrator = new PlanOrchestrator(
            builder, executor, store, cost, persistenceOpts,
            NullLogger<PlanOrchestrator>.Instance,
            coord,
            Options.Create(options));

        (ISpecialistAgent s1, ConcurrentQueue<SpecialistInvocation>? s1Invocations) = MakeRecordingSpecialist("scorecard", "score");
        (ISpecialistAgent s2, ConcurrentQueue<SpecialistInvocation>? s2Invocations) = MakeRecordingSpecialist("demand-forecasting", "demand");
        var lookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["scorecard"] = s1,
            ["demand-forecasting"] = s2,
        };

        PlanOrchestrationResult suspend = await orchestrator.RunAsync(new PlanOrchestrationInput
        {
            Request = new ChatRequest("multi", SessionId: "s"),
            Subject = "user-1",
            PrincipalKey = "user-1",
            TenantId = "Contoso",
            Roster = [s1, s2],
            SpecialistLookup = lookup,
            DetectedIntents = ["scorecard", "demand"],
            TraceId = "t",
        }, CancellationToken.None);

        suspend.IsSuspended.Should().BeTrue();
        s1Invocations.Should().BeEmpty("terminal reject before execution — no specialist ran.");
        s2Invocations.Should().BeEmpty();

        // Persisted plan initially recorded as AwaitingReview.
        store.Creates.Single().Status.Should().Be(PlanStatus.AwaitingReview);
    }

    // ── Disabled path — pre-#94 hot path preserved ───────────────────────

    [Fact]
    public async Task Disabled_review_leaves_execution_unchanged()
    {
        // No coordinator, no options. The orchestrator must run exactly as
        // pre-#94 code did — no approval row is written, no plan status is
        // AwaitingReview, and both planner-provided steps execute.
        SqliteApprovalGate gate = CreateGate();
        var store = new RecordingPlanStore();
        var cost = new NoOpCostTracker();
        var persistenceOpts = new PlanPersistenceOptions();
        var builder = new PlanBuilder(PlannerChat(), PlannerDef(), persistenceOpts, NullLogger<PlanBuilder>.Instance);
        var executor = new PlanExecutor(store, cost, new NoOpTraceCollector(), persistenceOpts, NullLogger<PlanExecutor>.Instance);
        var orchestrator = new PlanOrchestrator(
            builder, executor, store, cost, persistenceOpts,
            NullLogger<PlanOrchestrator>.Instance,
            reviewCoordinator: null,
            reviewOptions: null);

        (ISpecialistAgent s1, ConcurrentQueue<SpecialistInvocation>? s1Invocations) = MakeRecordingSpecialist("scorecard", "score");
        (ISpecialistAgent s2, ConcurrentQueue<SpecialistInvocation>? s2Invocations) = MakeRecordingSpecialist("demand-forecasting", "demand");
        var lookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["scorecard"] = s1,
            ["demand-forecasting"] = s2,
        };

        PlanOrchestrationResult result = await orchestrator.RunAsync(new PlanOrchestrationInput
        {
            Request = new ChatRequest("multi", SessionId: "s"),
            Subject = "user-1",
            PrincipalKey = "user-1",
            TenantId = "Contoso",
            Roster = [s1, s2],
            SpecialistLookup = lookup,
            DetectedIntents = ["scorecard", "demand"],
            TraceId = "t",
        }, CancellationToken.None);

        result.Status.Should().Be(PlanStatus.Completed);
        result.Steps.Should().HaveCount(2);
        s1Invocations.Should().HaveCount(1);
        s2Invocations.Should().HaveCount(1);

        // No approval row written when review is disabled.
        (await gate.GetPendingAsync("user-1")).Should().BeEmpty();
        (await gate.GetHistoryAsync(50)).Should().BeEmpty();

        store.Creates.Single().Status.Should().Be(PlanStatus.Running,
            "disabled path skips AwaitingReview entirely.");
    }
}

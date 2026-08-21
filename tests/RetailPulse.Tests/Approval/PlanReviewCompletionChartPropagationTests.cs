using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RetailPulse.Api.Agents.Planning;
using RetailPulse.Api.Approval;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Guardrails;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Observability;
using RetailPulse.Api.Persistence;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Approval;
using RetailPulse.Contracts.Guardrails;
using RetailPulse.Contracts.Observability;
using RetailPulse.Contracts.Persistence;
using RetailPulse.Contracts.Routing;
using RetailPulse.Contracts.Tracing;
using RetailPulse.Tests.Fixtures;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Tests.Approval;

/// <summary>
/// Wave 3 follow-up (#137) — Charts propagation across the plan-review
/// resume path. Prior to this suite, <c>PlanReviewCompletionService</c>
/// composed the final reply from specialist transcripts but silently
/// dropped every <see cref="ChartSpec"/> the executor produced during the
/// approved / edited / clarification-resumed run. The SignalR
/// <c>plan_final_response</c> broadcast delivered <c>reply</c> only, so
/// ADR-006's "9 chart types render on both paths" invariant broke as soon
/// as a plan needed reviewer approval.
///
/// This suite locks in the fix at three levels:
/// <list type="bullet">
///   <item>Every one of the 9 canonical chart types round-trips through the
///     resume path unchanged.</item>
///   <item>Multi-step charts are flattened in specialist order.</item>
///   <item>The <c>plan_final_response</c> broadcast payload carries the
///     same charts, so the frontend can render them without an extra
///     round-trip to <c>GET /api/plans/{planId}</c>.</item>
/// </list>
/// </summary>
public sealed class PlanReviewCompletionChartPropagationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _checkpointDir;

    public PlanReviewCompletionChartPropagationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"prv_charts_{Guid.NewGuid():N}.db");
        _checkpointDir = Path.Combine(Path.GetTempPath(), $"prv_charts_ckpt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_checkpointDir);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
        try { Directory.Delete(_checkpointDir, recursive: true); } catch { }
    }

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Canonical chart types the fast path renders. Every type must
    /// survive the resume path with matching type — matches
    /// PlanChartPropagationTests.KnownChartTypes.
    /// </summary>
    public static IEnumerable<object[]> KnownChartTypes =>
    [
        ["line"], ["bar"], ["groupedBar"], ["stackedBar"], ["horizontalBar"],
        ["pie"], ["donut"], ["gauge"], ["table"],
    ];

    [Theory]
    [MemberData(nameof(KnownChartTypes))]
    public async Task Approved_resume_preserves_specialist_ChartSpec_of_every_canonical_type(string chartType)
    {
        ChartSpec chart = MakeChart(chartType, $"{chartType} exemplar");

        (ServiceProvider sp, PlanOrchestrator orch, _,
            SqliteApprovalGate gate, _, CapturingHub hub) =
            BuildHost(specialistCharts: [chart]);

        PlanOrchestrationResult suspend = await orch.RunAsync(SampleInput(), default);
        suspend.IsSuspended.Should().BeTrue();

        ApprovalRequest row = (await gate.GetPendingAsync("user-1"))
            .Single(r => r.Context.PlanId == suspend.PlanId);
        await gate.RespondAsync(row.RequestId, ApprovalDecision.Approved, "go",
            JsonSerializer.Serialize(new PlanReviewResponsePayload
            {
                Kind = PlanReviewKinds.Approve,
            }, _json));

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();
        PlanReviewCompletionResult resume = await completion.ResolveAsync(suspend.PlanId, "user-1");

        resume.Kind.Should().Be(PlanReviewCompletionKind.Executed);
        resume.Charts.Should().NotBeEmpty(
            $"chart type '{chartType}' must survive the resume path — dropping it silently was the #137 defect.");
        resume.Charts.Should().Contain(c => c.Type == chartType);

        // SignalR broadcast must also carry the charts so the frontend
        // can render without an extra GET /api/plans/{id}. This is the
        // known follow-up called out on the #137 issue body.
        hub.LastFinalPayload.Should().NotBeNull(
            "the completion service must broadcast plan_final_response on the executed path.");
        IReadOnlyList<ChartSpec>? broadcastCharts = ExtractCharts(hub.LastFinalPayload);
        broadcastCharts.Should().NotBeNull(
            "plan_final_response must carry Charts alongside the final reply.");
        broadcastCharts.Should().Contain(c => c.Type == chartType);

        await sp.DisposeAsync();
    }

    [Fact]
    public async Task Approved_resume_flattens_multi_step_charts_in_specialist_order()
    {
        ChartSpec chartA = MakeChart("line", "A");
        ChartSpec chartB = MakeChart("gauge", "B");

        (ServiceProvider sp, PlanOrchestrator orch, _,
            SqliteApprovalGate gate, _, CapturingHub hub) =
            BuildHost(
                specialistChartsByKey: new Dictionary<string, IReadOnlyList<ChartSpec>?>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["scorecard"] = [chartA],
                    ["demand-forecasting"] = [chartB],
                });

        PlanOrchestrationResult suspend = await orch.RunAsync(SampleInput(), default);
        suspend.IsSuspended.Should().BeTrue();

        ApprovalRequest row = (await gate.GetPendingAsync("user-1"))
            .Single(r => r.Context.PlanId == suspend.PlanId);
        await gate.RespondAsync(row.RequestId, ApprovalDecision.Approved, "go",
            JsonSerializer.Serialize(new PlanReviewResponsePayload
            {
                Kind = PlanReviewKinds.Approve,
            }, _json));

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();
        PlanReviewCompletionResult resume = await completion.ResolveAsync(suspend.PlanId, "user-1");

        resume.Kind.Should().Be(PlanReviewCompletionKind.Executed);
        resume.Charts.Should().HaveCount(2,
            "both specialist charts must appear on the resumed plan response.");
        resume.Charts[0].Type.Should().Be("line", "specialist order must be preserved on resume.");
        resume.Charts[1].Type.Should().Be("gauge", "specialist order must be preserved on resume.");

        IReadOnlyList<ChartSpec>? broadcastCharts = ExtractCharts(hub.LastFinalPayload);
        broadcastCharts.Should().HaveCount(2);
        broadcastCharts[0].Type.Should().Be("line");
        broadcastCharts[1].Type.Should().Be("gauge");

        await sp.DisposeAsync();
    }

    [Fact]
    public async Task Edited_resume_preserves_specialist_charts_on_executed_result()
    {
        // The reviewer edits the plan — the effective plan runs against
        // the same specialist roster, so the same charts must reach the
        // final broadcast. Charts drop bug would silently regress here
        // because the edit path exercises ExecuteApprovedPlanAsync too.
        ChartSpec chart = MakeChart("bar", "edited");

        (ServiceProvider sp, PlanOrchestrator orch, _,
            SqliteApprovalGate gate, _, CapturingHub hub) =
            BuildHost(specialistCharts: [chart]);

        PlanOrchestrationResult suspend = await orch.RunAsync(SampleInput(), default);
        suspend.IsSuspended.Should().BeTrue();

        ApprovalRequest row = (await gate.GetPendingAsync("user-1"))
            .Single(r => r.Context.PlanId == suspend.PlanId);
        var edited = new List<PlanReviewStepDto>
        {
            new() { SpecialistKey = "scorecard", Intent = "scorecard", Action = "EDITED_ACTION" },
        };
        await gate.RespondAsync(row.RequestId, ApprovalDecision.Modified, "trim",
            JsonSerializer.Serialize(new PlanReviewResponsePayload
            {
                Kind = PlanReviewKinds.Edit,
                EditedSteps = edited,
            }, _json));

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();
        PlanReviewCompletionResult resume = await completion.ResolveAsync(suspend.PlanId, "user-1");

        resume.Kind.Should().Be(PlanReviewCompletionKind.Executed);
        resume.Charts.Should().Contain(c => c.Type == "bar",
            "edited-resume path must forward specialist charts just like the approve-resume path.");

        IReadOnlyList<ChartSpec>? broadcastCharts = ExtractCharts(hub.LastFinalPayload);
        broadcastCharts.Should().NotBeNull();
        broadcastCharts.Should().Contain(c => c.Type == "bar");

        await sp.DisposeAsync();
    }

    [Fact]
    public async Task Clarification_resume_preserves_specialist_charts_after_pause()
    {
        // Planner emits scorecard step 0 (returns a chart), then a
        // [[CLARIFY]] step at index 1. The executor runs scorecard, pauses
        // for clarification, and — per the pre-existing clarification
        // resume contract — hands the reviewer's answer to
        // ComposeFinalReply without re-invoking downstream steps. The chart
        // step 0 emitted BEFORE the pause must survive across the
        // clarification checkpoint and land on the final broadcast —
        // otherwise the plan silently drops charts every time a user has
        // to answer a clarification, which is the exact "silent artifact
        // dropping" #137 prohibits.
        ChartSpec chart = MakeChart("stackedBar", "pre-clar");

        (ServiceProvider sp, PlanOrchestrator orch, _,
            SqliteApprovalGate gate, _, CapturingHub hub) =
            BuildHost(
                specialistChartsByKey: new Dictionary<string, IReadOnlyList<ChartSpec>?>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["scorecard"] = [chart],
                    ["demand-forecasting"] = null,
                },
                plannerJson: PlannerJsonWithClarify());

        PlanOrchestrationResult suspend = await orch.RunAsync(SampleInput(), default);
        suspend.IsSuspended.Should().BeTrue();

        // Reviewer approves the initial plan; execution starts and pauses.
        ApprovalRequest reviewRow = (await gate.GetPendingAsync("user-1"))
            .Single(r => r.Context.PlanId == suspend.PlanId
                      && r.Context.Kind == ApprovalKind.PlanReview);
        await gate.RespondAsync(reviewRow.RequestId, ApprovalDecision.Approved, "go",
            JsonSerializer.Serialize(new PlanReviewResponsePayload
            {
                Kind = PlanReviewKinds.Approve,
            }, _json));

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();
        PlanReviewCompletionResult reviewResume = await completion.ResolveAsync(suspend.PlanId, "user-1");
        reviewResume.Kind.Should().Be(PlanReviewCompletionKind.SuspendedForClarification);

        // Reviewer answers the clarification.
        ApprovalRequest clarRow = (await gate.GetPendingAsync("user-1"))
            .Single(r => r.Context.Kind == ApprovalKind.Clarification
                      && r.Context.PlanId == suspend.PlanId);
        await gate.RespondAsync(clarRow.RequestId, ApprovalDecision.Approved, "ans",
            JsonSerializer.Serialize(new PlanClarificationAnswer { Answer = "west" }, _json));

        PlanReviewCompletionResult clarResume = await completion.ResolveAsync(suspend.PlanId, "user-1");
        clarResume.Kind.Should().Be(PlanReviewCompletionKind.Executed);
        clarResume.Charts.Should().Contain(c => c.Type == "stackedBar",
            "clarification-resume path must forward specialist charts emitted before the pause — they were previously discarded by the checkpoint.");

        IReadOnlyList<ChartSpec>? broadcastCharts = ExtractCharts(hub.LastFinalPayload);
        broadcastCharts.Should().NotBeNull();
        broadcastCharts.Should().Contain(c => c.Type == "stackedBar");

        await sp.DisposeAsync();
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    private static ChartSpec MakeChart(string type, string title) => new()
    {
        Type = type,
        Title = title,
        Data =
        [
            new ChartSeries
            {
                Legend = "series-a",
                Values = [new ChartDataPoint { X = "Q1", Y = 1 }, new ChartDataPoint { X = "Q2", Y = 2 }],
            },
        ],
    };

    /// <summary>
    /// Extract Charts from the anonymous payload the completion service
    /// broadcasts via SendAsync("plan_final_response", new { ... charts }).
    /// Reflection over the anonymous type keeps the test independent of the
    /// exact contract shape.
    /// </summary>
    private static IReadOnlyList<ChartSpec>? ExtractCharts(object? payload)
    {
        if (payload is null) return null;
        Type t = payload.GetType();
        System.Reflection.PropertyInfo? prop = t.GetProperty("charts")
            ?? t.GetProperty("Charts");
        return prop is null ? null : prop.GetValue(payload) as IReadOnlyList<ChartSpec>;
    }

    private (ServiceProvider Sp, PlanOrchestrator Orchestrator, PlanReviewCompletionServiceTests.InMemoryPlanStore PlanStore,
        SqliteApprovalGate Gate, ConcurrentQueue<string> Invocations, CapturingHub Hub)
        BuildHost(
            string? plannerJson = null,
            IReadOnlyList<ChartSpec>? specialistCharts = null,
            IReadOnlyDictionary<string, IReadOnlyList<ChartSpec>?>? specialistChartsByKey = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(TimeProvider.System);

        SqliteApprovalGate gate = new(_dbPath, NullLogger<SqliteApprovalGate>.Instance,
            TimeSpan.FromMinutes(30), TimeProvider.System);
        services.AddSingleton(gate);
        services.AddSingleton<IApprovalGate>(sp => sp.GetRequiredService<SqliteApprovalGate>());

        services.AddSingleton<ICheckpointStore<JsonElement>>(_ =>
                new FileSystemJsonCheckpointStore(new DirectoryInfo(_checkpointDir)));
        services.AddSingleton(sp =>
        {
            ICheckpointStore<JsonElement> store = sp.GetRequiredService<ICheckpointStore<JsonElement>>();
            return Microsoft.Agents.AI.Workflows.CheckpointManager.CreateJson(store, customOptions: null);
        });
        services.AddSingleton<PlanReviewCheckpointService>();

        var options = new PlanReviewOptions
        {
            Enabled = true,
            DefaultReviewTimeout = TimeSpan.FromSeconds(30),
            ClarificationTimeout = TimeSpan.FromSeconds(30),
            MaxReplanRounds = 1,
        };
        services.AddSingleton(Options.Create(options));

        services.AddSingleton<PlanClarifier>();
        services.AddSingleton<IPlanClarifier>(sp => sp.GetRequiredService<PlanClarifier>());
        services.AddSingleton<PlanReviewCoordinator>();

        var plans = new PlanReviewCompletionServiceTests.InMemoryPlanStore();
        services.AddSingleton<IPlanStore>(plans);

        var invocations = new ConcurrentQueue<string>();
        IReadOnlyList<ChartSpec>? scorecardCharts = specialistChartsByKey?.GetValueOrDefault("scorecard")
            ?? specialistCharts;
        IReadOnlyList<ChartSpec>? demandCharts = specialistChartsByKey?.GetValueOrDefault("demand-forecasting")
            ?? specialistCharts;
        ISpecialistAgent scorecard = MakeSpecialist("scorecard", invocations, "score-reply", scorecardCharts);
        ISpecialistAgent demand = MakeSpecialist("demand-forecasting", invocations, "demand-reply", demandCharts);
        services.AddSingleton(scorecard);
        services.AddSingleton(demand);

        var persistOpts = new PlanPersistenceOptions();
        services.AddSingleton(persistOpts);
        services.AddSingleton(new Api.Models.AgentDefinition
        {
            Key = "planner",
            Name = "Plan-First Orchestrator",
            Model = "gpt-test",
            SystemPrompt = "You are the planner.",
            Temperature = 0.1,
        });
        services.AddSingleton(sp => new PlanBuilder(
            AgentTestFixtures.CreateMockChatClient(plannerJson ?? DefaultPlannerJson()),
            sp.GetRequiredService<Api.Models.AgentDefinition>(),
            persistOpts,
            NullLogger<PlanBuilder>.Instance));

        services.AddSingleton<ICostTracker>(new NoOpCostTracker());
        services.AddSingleton<ITraceCollector>(new NoOpTraceCollector());
        services.AddSingleton(sp => new PlanExecutor(
            sp.GetRequiredService<IPlanStore>(),
            sp.GetRequiredService<ICostTracker>(),
            sp.GetRequiredService<ITraceCollector>(),
            sp.GetRequiredService<PlanPersistenceOptions>(),
            NullLogger<PlanExecutor>.Instance,
            sp.GetService<PlanClarifier>(),
            sp.GetService<PlanReviewCoordinator>()));

        services.AddSingleton(sp => new PlanOrchestrator(
            sp.GetRequiredService<PlanBuilder>(),
            sp.GetRequiredService<PlanExecutor>(),
            sp.GetRequiredService<IPlanStore>(),
            sp.GetRequiredService<ICostTracker>(),
            persistOpts,
            NullLogger<PlanOrchestrator>.Instance,
            sp.GetService<PlanReviewCoordinator>(),
            Options.Create(options)));

        services.AddSingleton(new GuardrailsConfig
        {
            PiiDetectionEnabled = false,
            AutoRedactPii = false,
            ContentSafety = new ContentSafetyConfig { Enabled = false },
        });
        services.AddSingleton<ISuspiciousRequestLog, InMemorySuspiciousRequestLog>();
        services.AddSingleton<ITenantProvider>(new StubTenantProvider());
        services.AddSingleton<GuardrailsMiddleware>();
        services.AddSingleton<IAuditLog, InMemoryAuditLog>();
        services.AddSingleton(_ => new ConversationExporter(
            Options.Create(new ObservabilityOptions())));

        var capturingHub = new CapturingHub();
        services.AddSingleton<IHubContext<TelemetryHub>>(capturingHub);

        services.AddSingleton<PlanReviewCompletionService>();

        ServiceProvider sp2 = services.BuildServiceProvider();
        return (sp2,
            sp2.GetRequiredService<PlanOrchestrator>(),
            plans,
            sp2.GetRequiredService<SqliteApprovalGate>(),
            invocations,
            capturingHub);
    }

    private static PlanOrchestrationInput SampleInput()
    {
        ISpecialistAgent scorecard = MakeSpecialist("scorecard", new(), "", null);
        ISpecialistAgent demand = MakeSpecialist("demand-forecasting", new(), "", null);
        return new PlanOrchestrationInput
        {
            Request = new ChatRequest("multi", SessionId: "s"),
            Subject = "user-1",
            PrincipalKey = "user-1",
            TenantId = "Contoso",
            Roster = [scorecard, demand],
            SpecialistLookup = new Dictionary<string, ISpecialistAgent>(StringComparer.OrdinalIgnoreCase)
            {
                ["scorecard"] = scorecard,
                ["demand-forecasting"] = demand,
            },
            DetectedIntents = ["scorecard", "demand"],
            TraceId = "t",
        };
    }

    private static string DefaultPlannerJson() => /*lang=json,strict*/ @"{ ""steps"": [
        { ""specialist_key"": ""scorecard"", ""intent"": ""scorecard"", ""action"": ""ORIGINAL_ACTION"" },
        { ""specialist_key"": ""demand-forecasting"", ""intent"": ""demand"", ""action"": ""ORIGINAL_DEMAND_ACTION"" }
    ] }";

    private static string PlannerJsonWithClarify() => /*lang=json,strict*/ @"{ ""steps"": [
        { ""specialist_key"": ""scorecard"", ""intent"": ""scorecard"", ""action"": ""step-0"" },
        { ""specialist_key"": ""scorecard"", ""intent"": ""scorecard"", ""action"": ""[[CLARIFY]] Which region?"" },
        { ""specialist_key"": ""demand-forecasting"", ""intent"": ""demand"", ""action"": ""step-2"" }
    ] }";

    private static ISpecialistAgent MakeSpecialist(
        string key,
        ConcurrentQueue<string> invocations,
        string reply,
        IReadOnlyList<ChartSpec>? charts)
    {
        var m = new Mock<ISpecialistAgent>();
        m.SetupGet(a => a.Key).Returns(key);
        m.SetupGet(a => a.DisplayName).Returns(key);
        m.SetupGet(a => a.Model).Returns("gpt-test");
        m.SetupGet(a => a.SupportedIntents).Returns([key]);
        m.Setup(a => a.HandleAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .Returns((ChatRequest req, CancellationToken _) =>
            {
                invocations.Enqueue(req.Message);
                List<ChartSpec>? chartList = charts is null ? null : [.. charts];
                return Task.FromResult(new ChatResponse(
                    string.IsNullOrEmpty(reply) ? $"{key}-reply" : reply,
                    req.SessionId ?? "s", [], chartList, 10, new TokenUsage(1, 1, 2)));
            });
        return m.Object;
    }

    // ── Support doubles ──────────────────────────────────────────────────

    /// <summary>
    /// Captures the last <c>plan_final_response</c> payload so tests can
    /// assert the SignalR broadcast shape without a real hub connection.
    /// </summary>
    private sealed class CapturingHub : IHubContext<TelemetryHub>
    {
        public object? LastFinalPayload { get; private set; }

        public IHubClients Clients { get; }
        public IGroupManager Groups { get; } = new StubGroupManager();

        public CapturingHub()
        {
            Clients = new CapturingClients(this);
        }

        private sealed class CapturingClients(CapturingHub owner) : IHubClients
        {
            public IClientProxy All { get; } = new CapturingProxy(owner);
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new CapturingProxy(owner);
            public IClientProxy Client(string connectionId) => new CapturingProxy(owner);
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new CapturingProxy(owner);
            public IClientProxy Group(string groupName) => new CapturingProxy(owner);
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new CapturingProxy(owner);
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => new CapturingProxy(owner);
            public IClientProxy User(string userId) => new CapturingProxy(owner);
            public IClientProxy Users(IReadOnlyList<string> userIds) => new CapturingProxy(owner);
        }

        private sealed class CapturingProxy(CapturingHub owner) : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            {
                if (string.Equals(method, "plan_final_response", StringComparison.Ordinal) && args.Length > 0)
                {
                    owner.LastFinalPayload = args[0];
                }
                return Task.CompletedTask;
            }
        }

        private sealed class StubGroupManager : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
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

    private sealed class StubTenantProvider : ITenantProvider
    {
        public TenantConfiguration GetTenant() => new()
        {
            Company = "Contoso",
            Industry = "test",
        };
    }
}

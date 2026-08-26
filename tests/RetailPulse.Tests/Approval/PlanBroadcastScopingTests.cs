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
using RetailPulse.Tests.TestInfrastructure;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Tests.Approval;

/// <summary>
/// Adversarial delivery + clarification-resume regression coverage for
/// issue #141. The plan-completion path used to broadcast
/// <c>plan_final_response</c>, <c>plan_review_next_round</c>, and
/// <c>plan_review_resolved</c> via <see cref="IHubClients.All"/>, which
/// leaked another subject's plan payload — including <c>reply</c> and
/// <c>charts</c> — to every connected SignalR client.
///
/// <para>
/// This suite pins:
/// </para>
/// <list type="bullet">
///   <item>Two distinct subjects on distinct sessions: subject B receives
///     zero plan payloads when subject A's plan resolves, and
///     <see cref="IHubClients.All"/> is never invoked for a plan event.</item>
///   <item>A resolved plan without an owning session id fails closed —
///     nothing is broadcast, no fallback to <c>Clients.All</c>.</item>
///   <item>Mid-plan clarification: the first specialist AFTER the pause
///     runs on resume — the previous index-rebase bug silently skipped it.</item>
/// </list>
///
/// <para>
/// The hub fake used here (<see cref="RecordingHub"/>) issues a DISTINCT
/// proxy per All / Group / Client target so a delivery-scope assertion is
/// meaningful — the earlier <c>CapturingHub</c> in this project shared one
/// proxy across <c>All</c> and <c>Group</c>, which would have made this
/// entire suite vacuous.
/// </para>
/// </summary>
public sealed class PlanBroadcastScopingTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _checkpointDir;

    public PlanBroadcastScopingTests()
    {
        _dbPath = SqliteTestCleanup.NewDbPath("prv_scope");
        _checkpointDir = Path.Combine(Path.GetTempPath(), $"prv_scope_ckpt_{Guid.NewGuid():N}");
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

    // Adversarial delivery: two subjects on distinct sessions

    [Fact]
    public async Task Subject_B_on_a_different_session_receives_zero_plan_payloads_when_subject_A_plan_completes()
    {
        const string sessionA = "session-A";
        const string sessionB = "session-B";
        const string subjectA = "user-A";

        (ServiceProvider sp, PlanOrchestrator orch, _,
            SqliteApprovalGate gate, _, RecordingHub hub) = BuildHost();

        PlanOrchestrationResult suspend = await orch.RunAsync(
            SampleInput(subjectA, sessionA), default);
        suspend.IsSuspended.Should().BeTrue();

        ApprovalRequest row = (await gate.GetPendingAsync(subjectA))
            .Single(r => r.Context.PlanId == suspend.PlanId);
        await gate.RespondAsync(row.RequestId, ApprovalDecision.Approved, "go",
            JsonSerializer.Serialize(new PlanReviewResponsePayload
            {
                Kind = PlanReviewKinds.Approve,
            }, _json));

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();
        PlanReviewCompletionResult resume = await completion.ResolveAsync(suspend.PlanId, subjectA);
        resume.Kind.Should().Be(PlanReviewCompletionKind.Executed);

        // Owning session group DID receive the final response.
        hub.GroupSends.Should().ContainKey(sessionA);
        hub.GroupSends[sessionA].Should().Contain(
            s => s.Method == "plan_final_response",
            "the plan's owning session must receive its own final response.");

        // Subject B's group received nothing at all — no plan_final_response,
        // no plan_review_next_round, no plan_review_resolved.
        (hub.GroupSends.TryGetValue(sessionB, out List<RecordingHub.SendCall>? bSends) ? bSends : [])
            .Where(s => IsPlanEvent(s.Method))
            .Should().BeEmpty(
                "a second subject on a different session must never receive another subject's plan payload — this is the cross-session leak #141 fixes.");

        // And Clients.All must NOT have been used for any plan event.
        hub.AllSends
            .Where(s => IsPlanEvent(s.Method))
            .Should().BeEmpty(
                "no plan-path SignalR event may ever go to Clients.All; delivery must be session-scoped or suppressed.");

        await sp.DisposeAsync();
    }

    [Fact]
    public async Task Final_response_carries_charts_only_to_owning_session_group()
    {
        const string sessionA = "session-A";
        const string subjectA = "user-A";
        ChartSpec chart = new()
        {
            Type = "bar",
            Title = "leaked?",
            Data =
            [
                new ChartSeries
                {
                    Legend = "s",
                    Values = [new ChartDataPoint { X = "Q1", Y = 10 }],
                },
            ],
        };

        (ServiceProvider sp, PlanOrchestrator orch, _,
            SqliteApprovalGate gate, _, RecordingHub hub) =
            BuildHost(specialistCharts: [chart]);

        PlanOrchestrationResult suspend = await orch.RunAsync(
            SampleInput(subjectA, sessionA), default);
        ApprovalRequest row = (await gate.GetPendingAsync(subjectA))
            .Single(r => r.Context.PlanId == suspend.PlanId);
        await gate.RespondAsync(row.RequestId, ApprovalDecision.Approved, "go",
            JsonSerializer.Serialize(new PlanReviewResponsePayload
            {
                Kind = PlanReviewKinds.Approve,
            }, _json));

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();
        PlanReviewCompletionResult resume = await completion.ResolveAsync(suspend.PlanId, subjectA);
        resume.Kind.Should().Be(PlanReviewCompletionKind.Executed);

        RecordingHub.SendCall final = hub.GroupSends[sessionA]
            .Single(s => s.Method == "plan_final_response");
        ExtractCharts(final.Payload)
            .Should().Contain(c => c.Type == "bar",
                "review-resume broadcast must still carry Charts alongside the reply so the frontend can render without an extra GET.");

        hub.AllSends.Should().BeEmpty(
            "Clients.All must not receive plan payloads even when charts are present.");

        await sp.DisposeAsync();
    }

    [Fact]
    public async Task Missing_session_identity_suppresses_broadcast_and_never_falls_back_to_all()
    {
        const string subjectA = "user-A";

        (ServiceProvider sp, PlanOrchestrator orch, _,
            SqliteApprovalGate gate, _, RecordingHub hub) = BuildHost();

        PlanOrchestrationResult suspend = await orch.RunAsync(
            SampleInput(subjectA, sessionId: null), default);

        ApprovalRequest row = (await gate.GetPendingAsync(subjectA))
            .Single(r => r.Context.PlanId == suspend.PlanId);
        await gate.RespondAsync(row.RequestId, ApprovalDecision.Approved, "go",
            JsonSerializer.Serialize(new PlanReviewResponsePayload
            {
                Kind = PlanReviewKinds.Approve,
            }, _json));

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();
        PlanReviewCompletionResult resume = await completion.ResolveAsync(suspend.PlanId, subjectA);
        resume.Kind.Should().Be(PlanReviewCompletionKind.Executed);

        hub.AllSends
            .Where(s => IsPlanEvent(s.Method))
            .Should().BeEmpty(
                "missing session identity must fail closed — no Clients.All fallback.");

        hub.GroupSends.Values.SelectMany(v => v)
            .Where(s => IsPlanEvent(s.Method))
            .Should().BeEmpty(
                "with no session identity, no plan payload may be delivered at all.");

        await sp.DisposeAsync();
    }

    // Clarification-resume index bug (#141)

    [Fact]
    public async Task Clarification_resume_executes_the_post_pause_specialist()
    {
        const string sessionA = "session-A";
        const string subjectA = "user-A";

        (ServiceProvider sp, PlanOrchestrator orch, _,
            SqliteApprovalGate gate, ConcurrentQueue<string> invocations,
            RecordingHub hub) =
            BuildHost(plannerJson: PlannerJsonWithClarify());

        PlanOrchestrationResult suspend = await orch.RunAsync(
            SampleInput(subjectA, sessionA), default);
        suspend.IsSuspended.Should().BeTrue();

        ApprovalRequest reviewRow = (await gate.GetPendingAsync(subjectA))
            .Single(r => r.Context.PlanId == suspend.PlanId
                      && r.Context.Kind == ApprovalKind.PlanReview);
        await gate.RespondAsync(reviewRow.RequestId, ApprovalDecision.Approved, "go",
            JsonSerializer.Serialize(new PlanReviewResponsePayload
            {
                Kind = PlanReviewKinds.Approve,
            }, _json));

        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();
        PlanReviewCompletionResult reviewResume = await completion.ResolveAsync(suspend.PlanId, subjectA);
        reviewResume.Kind.Should().Be(PlanReviewCompletionKind.SuspendedForClarification);

        int invocationsBeforeAnswer = invocations.Count;

        ApprovalRequest clarRow = (await gate.GetPendingAsync(subjectA))
            .Single(r => r.Context.Kind == ApprovalKind.Clarification
                      && r.Context.PlanId == suspend.PlanId);
        await gate.RespondAsync(clarRow.RequestId, ApprovalDecision.Approved, "ans",
            JsonSerializer.Serialize(new PlanClarificationAnswer { Answer = "west" }, _json));

        PlanReviewCompletionResult clarResume = await completion.ResolveAsync(suspend.PlanId, subjectA);
        clarResume.Kind.Should().Be(PlanReviewCompletionKind.Executed);

        int invocationsAfterAnswer = invocations.Count;
        (invocationsAfterAnswer - invocationsBeforeAnswer).Should().BeGreaterThan(0,
            "the clarification-resume path must execute at least one specialist AFTER the pause.");

        invocations.Should().Contain(s => s.Contains("step-2", StringComparison.Ordinal),
            "the first specialist after the pause (demand-forecasting, step-2) must run on resume — the index-rebase bug used to skip it silently.");

        clarResume.Reply.Should().Contain("demand-reply",
            "the post-pause specialist's transcript must appear in the composed reply.");

        hub.GroupSends[sessionA].Should().Contain(s => s.Method == "plan_final_response");
        hub.AllSends.Where(s => IsPlanEvent(s.Method)).Should().BeEmpty();

        await sp.DisposeAsync();
    }

    // Support

    private static bool IsPlanEvent(string method) =>
        method is "plan_final_response"
              or "plan_review_next_round"
              or "plan_review_resolved";

    private static IReadOnlyList<ChartSpec>? ExtractCharts(object? payload)
    {
        if (payload is null) return null;
        Type t = payload.GetType();
        System.Reflection.PropertyInfo? prop = t.GetProperty("charts")
            ?? t.GetProperty("Charts");
        return prop is null ? null : prop.GetValue(payload) as IReadOnlyList<ChartSpec>;
    }

    private static PlanOrchestrationInput SampleInput(string subject, string? sessionId)
    {
        ISpecialistAgent scorecard = MakeSpecialist("scorecard", new(), "score-reply", null);
        ISpecialistAgent demand = MakeSpecialist("demand-forecasting", new(), "demand-reply", null);
        return new PlanOrchestrationInput
        {
            Request = new ChatRequest("multi", SessionId: sessionId),
            Subject = subject,
            PrincipalKey = subject,
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

    private (ServiceProvider Sp, PlanOrchestrator Orchestrator, PlanReviewCompletionServiceTests.InMemoryPlanStore PlanStore,
        SqliteApprovalGate Gate, ConcurrentQueue<string> Invocations, RecordingHub Hub)
        BuildHost(
            string? plannerJson = null,
            IReadOnlyList<ChartSpec>? specialistCharts = null)
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
        ISpecialistAgent scorecard = MakeSpecialist("scorecard", invocations, "score-reply", specialistCharts);
        ISpecialistAgent demand = MakeSpecialist("demand-forecasting", invocations, "demand-reply", specialistCharts);
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

        var hub = new RecordingHub();
        services.AddSingleton<IHubContext<TelemetryHub>>(hub);

        services.AddSingleton<PlanReviewCompletionService>();

        ServiceProvider sp2 = services.BuildServiceProvider();
        return (sp2,
            sp2.GetRequiredService<PlanOrchestrator>(),
            plans,
            sp2.GetRequiredService<SqliteApprovalGate>(),
            invocations,
            hub);
    }

    /// <summary>
    /// A minimal <see cref="IHubContext{THub}"/> fake that gives each
    /// delivery target its OWN <see cref="IClientProxy"/> and records every
    /// SendAsync so tests can assert whether a payload landed on
    /// <c>Clients.All</c>, on a specific <c>Group(name)</c>, or on a
    /// specific <c>Client(id)</c>. Sharing a single proxy across targets
    /// would let a broadcast to <c>All</c> pass a test that only asserted
    /// against a group name and vice-versa — see the tightening called out
    /// in the #141 design review contract.
    /// </summary>
    private sealed class RecordingHub : IHubContext<TelemetryHub>
    {
        public List<SendCall> AllSends { get; } = [];
        public Dictionary<string, List<SendCall>> GroupSends { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<SendCall>> ClientSends { get; } = new(StringComparer.Ordinal);

        public IHubClients Clients { get; }
        public IGroupManager Groups { get; } = new StubGroupManager();

        public RecordingHub()
        {
            Clients = new RecordingClients(this);
        }

        public sealed record SendCall(string Method, object? Payload);

        private sealed class RecordingClients(RecordingHub owner) : IHubClients
        {
            public IClientProxy All { get; } = new BucketProxy(owner.AllSends);
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new BucketProxy(owner.AllSends);
            public IClientProxy Client(string connectionId) =>
                new BucketProxy(owner.ClientSends.TryGetValue(connectionId, out List<SendCall>? list)
                    ? list
                    : owner.ClientSends[connectionId] = []);
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new BucketProxy([]);
            public IClientProxy Group(string groupName) =>
                new BucketProxy(owner.GroupSends.TryGetValue(groupName, out List<SendCall>? list)
                    ? list
                    : owner.GroupSends[groupName] = []);
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) =>
                new BucketProxy(owner.GroupSends.TryGetValue(groupName, out List<SendCall>? list)
                    ? list
                    : owner.GroupSends[groupName] = []);
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => new BucketProxy([]);
            public IClientProxy User(string userId) => new BucketProxy([]);
            public IClientProxy Users(IReadOnlyList<string> userIds) => new BucketProxy([]);
        }

        private sealed class BucketProxy(List<SendCall> bucket) : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            {
                lock (bucket)
                {
                    bucket.Add(new SendCall(method, args.Length > 0 ? args[0] : null));
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

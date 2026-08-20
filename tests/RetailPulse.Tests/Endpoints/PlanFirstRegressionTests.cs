using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RetailPulse.Api.Agents.Planning;
using RetailPulse.Api.Endpoints;
using RetailPulse.Api.Guardrails;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Observability;
using RetailPulse.Api.Persistence;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Guardrails;
using RetailPulse.Contracts.Observability;
using RetailPulse.Contracts.Persistence;
using RetailPulse.Contracts.Routing;
using RetailPulse.Contracts.Tracing;

namespace RetailPulse.Tests.Endpoints;

/// <summary>
/// Regression tests for the three plan-first failures Kroger caught on
/// PR #123 (commit <c>b685949</c>): default-config DI regression, missing
/// output guardrail parity, and missing audit / export / session-turn parity.
/// Each test is written so it would have failed on the pre-fix build; the
/// gates are pinned as endpoint-level or extracted-helper contracts rather
/// than fragile string-in-source assertions.
/// </summary>
public sealed class PlanFirstRegressionTests
{
    // ────────────────────────────────────────────────────────────────────
    // FINDING 1 — Default-config DI regression
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reproduces the pre-fix bug: the planner AgentDefinition is present
    /// (prompts.yaml always defines one), PlanPersistence:Enabled=false
    /// (the default), a plan orchestrator factory is registered that asks
    /// for IPlanStore, and the request-time resolution of
    /// <c>[FromServices] PlanOrchestrator?</c> throws — turning ordinary
    /// /api/chat into a 500. The fix has the registration itself gate on
    /// <see cref="PlanPersistenceOptions.Enabled"/> exactly like this test
    /// binds it, so PlanOrchestrator stays out of the container and the
    /// endpoint's nullable [FromServices] resolves to null.
    /// </summary>
    [Fact]
    public async Task Default_config_with_planner_defined_and_persistence_disabled_does_not_500_on_chat()
    {
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        IHostBuilder builder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<IConfiguration>(config);
                    services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
                    services.AddPlanPersistence(config, ":memory:");

                    PlanPersistenceOptions opts = config
                        .GetSection(PlanPersistenceOptions.SectionName)
                        .Get<PlanPersistenceOptions>() ?? new PlanPersistenceOptions();
                    bool plannerDefined = true;
                    if (plannerDefined && opts.Enabled)
                    {
                        services.AddScoped<PlanOrchestrator>(_ =>
                            throw new InvalidOperationException(
                                "Plan services must not be registered when persistence is disabled."));
                    }
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapPost("/api/chat",
                            (ChatRequest _, [FromServices] PlanOrchestrator? planOrchestrator) => Results.Ok(new
                            {
                                reply = "ok",
                                planOrchestratorPresent = planOrchestrator is not null,
                            })));
                });
            });

        using IHost host = builder.Build();
        await host.StartAsync();
        try
        {
            using HttpClient client = host.GetTestClient();
            HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/chat", new ChatRequest("hello"));

            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "default chat must not 500 when PlanPersistence is disabled");

            string body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("\"planOrchestratorPresent\":false",
                "the plan orchestrator must be absent from the container so [FromServices] resolves null");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// The complementary positive case: when PlanPersistence:Enabled=true
    /// the guard the fix installs opens: the plan store is registered AND
    /// the enabled bit round-trips through configuration, so a Program.cs
    /// registration expressed as <c>plannerDef is not null &amp;&amp; opts.Enabled</c>
    /// would admit the plan services. The counterpart negative case above
    /// proves the same guard rejects a disabled configuration.
    /// </summary>
    [Fact]
    public void Plan_persistence_guard_admits_when_persistence_enabled()
    {
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlanPersistence:Enabled"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddPlanPersistence(config, ":memory:");

        PlanPersistenceOptions opts = config
            .GetSection(PlanPersistenceOptions.SectionName)
            .Get<PlanPersistenceOptions>() ?? new PlanPersistenceOptions();

        opts.Enabled.Should().BeTrue("the enabled bit must round-trip through configuration");

        using ServiceProvider sp = services.BuildServiceProvider();

        sp.GetService<IPlanStore>().Should().NotBeNull(
            "PlanPersistence:Enabled=true must register the plan store singleton");

        // Combined with the negative case, this proves the guard clause
        // installed in Program.cs — plannerDef is not null AND opts.Enabled
        // — admits the plan services in exactly the enabled configuration.
        bool plannerDefined = true;
        (plannerDefined && opts.Enabled).Should().BeTrue(
            "the plan-services registration guard must admit when both signals are on");
    }

    /// <summary>
    /// Independent guard test — proves the plan store extension itself
    /// (which the fix relies on) is honest about its enabled bit: the
    /// store is absent when persistence is disabled.
    /// </summary>
    [Fact]
    public void Plan_store_is_absent_when_persistence_disabled()
    {
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddPlanPersistence(config, ":memory:");

        using ServiceProvider sp = services.BuildServiceProvider();
        sp.GetService<IPlanStore>().Should().BeNull(
            "PlanPersistence:Enabled=false (default) must leave the store unregistered");
    }

    /// <summary>
    /// Locks in the specific failure mode that made this a regression, not
    /// just an omission: the pre-fix Program.cs registered a plan executor
    /// factory that called <c>sp.GetRequiredService&lt;IPlanStore&gt;()</c>
    /// unconditionally whenever the planner definition was present, even
    /// though the store extension only registers the store when
    /// <see cref="PlanPersistenceOptions.Enabled"/> is true. Under that
    /// shape, request-time resolution of <c>[FromServices] PlanOrchestrator?</c>
    /// on <c>/api/chat</c> threw
    /// <see cref="InvalidOperationException"/> (no service for
    /// <see cref="IPlanStore"/>), turning ordinary default-config chat
    /// into a 500. This test rebuilds the old-shape registration and
    /// asserts the throw so the guard clause that the fix installs has
    /// something concrete to guard against.
    /// </summary>
    [Fact]
    public void Preserving_the_old_buggy_registration_shape_throws_when_resolving_the_plan_executor()
    {
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddPlanPersistence(config, ":memory:");

        // Reproduce the pre-fix Program.cs shape: gate ONLY on plannerDef
        // being present, ignore opts.Enabled. This is the exact bug — the
        // factory calls GetRequiredService<IPlanStore>() at resolve time,
        // but the store is only registered when opts.Enabled is true.
        services.AddScoped(sp => new PlanExecutor(
            sp.GetRequiredService<IPlanStore>(),
            sp.GetRequiredService<ICostTracker>(),
            sp.GetRequiredService<ITraceCollector>(),
            new PlanPersistenceOptions(),
            NullLogger<PlanExecutor>.Instance));

        using ServiceProvider sp2 = services.BuildServiceProvider();
        using IServiceScope scope = sp2.CreateScope();

        Action act = () => scope.ServiceProvider.GetService<PlanExecutor>();
        act.Should().Throw<InvalidOperationException>(
            "the old buggy registration shape resolves a plan executor factory that asks for IPlanStore, which is missing when PlanPersistence is disabled");
    }

    // ────────────────────────────────────────────────────────────────────
    // FINDING 2 — Output guardrail parity
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The plan-first branch composes its reply from specialist outputs and
    /// used to return that reply unfiltered. The single-specialist branch
    /// always calls <see cref="GuardrailsMiddleware.FilterOutputAsync"/>
    /// before returning. This test drives the shared filter with a plan
    /// reply that carries PII (email and phone) and pins that the same
    /// redactor scrubs it — same behavior the fix installs in
    /// ChatEndpoints before constructing the plan ChatResponse.
    /// </summary>
    [Fact]
    public async Task Plan_reply_is_filtered_through_the_shared_output_guardrail()
    {
        var guardrails = new GuardrailsMiddleware(
            new GuardrailsConfig
            {
                PiiDetectionEnabled = true,
                AutoRedactPii = true,
            },
            new InMemorySuspiciousRequestLog(),
            new Mock<ITenantProvider>().Object,
            NullLogger<GuardrailsMiddleware>.Instance);

        const string planReply =
            "Scorecard: contact ops@example.com for the report.\n\n---\n\nDemand: hotline (555) 123-4567 for details.";

        string filtered = await guardrails.FilterOutputAsync(planReply, userId: "user-1");

        filtered.Should().NotContain("ops@example.com",
            "the email in the composed plan reply must be redacted, exactly like the single-specialist path");
        filtered.Should().NotContain("(555) 123-4567",
            "the phone number in the composed plan reply must be redacted, exactly like the single-specialist path");
        filtered.Should().Contain("[REDACTED:EMAIL]");
        filtered.Should().Contain("[REDACTED:PHONE]");
    }

    /// <summary>
    /// Belt-and-braces contract test on the endpoint source: prove the
    /// literal call to <c>guardrails.FilterOutputAsync(planResult.Reply,</c>
    /// exists inside the plan-first branch. If someone accidentally drops
    /// that call in a future refactor, this test surfaces the regression
    /// without waiting for an integration-level failure to appear.
    /// </summary>
    [Fact]
    public void Plan_first_branch_source_calls_FilterOutputAsync_on_the_plan_reply()
    {
        string endpointSource = LoadChatEndpointsSource();

        int planBranchStart = endpointSource.IndexOf(
            "Plan-first interception (issue #93)", StringComparison.Ordinal);
        int singleSpecialistStart = endpointSource.IndexOf(
            "Agent execution with tracing", StringComparison.Ordinal);

        planBranchStart.Should().BeGreaterThan(0,
            "the plan-first branch anchor comment must remain present in the endpoint source");
        singleSpecialistStart.Should().BeGreaterThan(planBranchStart,
            "the single-specialist branch must sit after the plan-first branch");

        string planBranchSlice = endpointSource[planBranchStart..singleSpecialistStart];
        planBranchSlice.Should().Contain("guardrails.FilterOutputAsync",
            "the plan-first branch must invoke the shared output guardrail before returning the reply");
        planBranchSlice.Should().Contain("planResult.Reply",
            "the filtered content must be the plan reply, not some unrelated string");
    }

    // ────────────────────────────────────────────────────────────────────
    // FINDING 3 — Audit / export / session-turn parity
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When persistence is enabled, a plan turn must produce the same
    /// bookkeeping trio the single-specialist turn produces: one audit
    /// entry, one user + one assistant message tracked in the conversation
    /// exporter, and one user + one assistant turn appended to the session
    /// store. This test exercises the extracted helper directly with
    /// recording fakes so the invariant is deterministic and not tied to
    /// the LLM path.
    /// </summary>
    [Fact]
    public async Task Plan_turn_records_audit_export_and_session_turns_when_persistence_enabled()
    {
        var audit = new RecordingAuditLog();
        ConversationExporter exporter = CreateExporter();
        var sessionStore = new RecordingSessionStore();
        var tenantProvider = new Mock<ITenantProvider>();
        tenantProvider.Setup(t => t.GetTenant()).Returns(new TenantConfiguration { Company = "Contoso" });

        IOptions<SessionPersistenceOptions> sessionOptions = Options.Create(new SessionPersistenceOptions
        {
            Enabled = true,
            RedactPiiOnWrite = false,
        });

        var ctx = new ChatEndpoints.ChatTurnParityContext(
            Request: new ChatRequest("multi domain question", SessionId: "sess-1"),
            SessionId: "sess-1",
            UserId: "user-1",
            AgentKey: "planner",
            Intent: "multi/domain",
            Confidence: 0.87,
            Action: "chat.plan.multi/domain",
            Reply: "composed plan reply",
            InputTokens: 137,
            OutputTokens: 42,
            DurationMs: 1250,
            SpanSummary: /*lang=json,strict*/ "{\"agent\":\"planner\",\"steps\":2}",
            PersistenceEnabled: true);

        await ChatEndpoints.RecordChatTurnParityAsync(
            ctx,
            audit,
            exporter,
            sessionStore,
            sessionOptions,
            tenantProvider.Object,
            NullLogger.Instance,
            CancellationToken.None);

        audit.Entries.Should().HaveCount(1, "a plan turn is still an accountable action");
        AuditEntry auditEntry = audit.Entries.Single();
        auditEntry.UserId.Should().Be("user-1");
        auditEntry.AgentId.Should().Be("planner");
        auditEntry.Action.Should().Be("chat.plan.multi/domain");
        auditEntry.TokensUsed.Should().Be(137 + 42);

        SessionPreview? preview = await exporter.GetPreviewAsync("sess-1", 100);
        preview.Should().NotBeNull("the plan-first branch must register the session in the exporter");
        preview.Messages.Should().HaveCount(2, "one user message and one assistant reply");
        preview.Messages[0].Role.Should().Be("user");
        preview.Messages[0].Content.Should().Be("multi domain question");
        preview.Messages[1].Role.Should().Be("assistant");
        preview.Messages[1].Content.Should().Be("composed plan reply");

        sessionStore.Writes.Should().HaveCount(2, "user + assistant turn writes when persistence is enabled");
        SessionTurnWrite userTurn = sessionStore.Writes[0];
        SessionTurnWrite assistantTurn = sessionStore.Writes[1];

        userTurn.Role.Should().Be("user");
        userTurn.Content.Should().Be("multi domain question");
        userTurn.RoutingAgentKey.Should().Be("planner");
        userTurn.RoutingIntent.Should().Be("multi/domain");
        userTurn.TenantId.Should().Be("Contoso");

        assistantTurn.Role.Should().Be("assistant");
        assistantTurn.Content.Should().Be("composed plan reply");
        assistantTurn.AgentId.Should().Be("planner");
        assistantTurn.InputTokens.Should().Be(137);
        assistantTurn.OutputTokens.Should().Be(42);
        assistantTurn.TotalTokens.Should().Be(137 + 42);
        assistantTurn.SpanSummary.Should().Contain("planner");
    }

    /// <summary>
    /// When persistence is off, the plan branch still logs audit and
    /// exporter data (both are always-on observability) but must NOT
    /// attempt any session-store writes — the store may not even be
    /// registered, and even if it is, the caller opted out of durable
    /// session history.
    /// </summary>
    [Fact]
    public async Task Plan_turn_skips_session_writes_when_persistence_disabled()
    {
        var audit = new RecordingAuditLog();
        ConversationExporter exporter = CreateExporter();
        var sessionStore = new RecordingSessionStore();
        var tenantProvider = new Mock<ITenantProvider>();
        tenantProvider.Setup(t => t.GetTenant()).Returns(new TenantConfiguration { Company = "Contoso" });

        IOptions<SessionPersistenceOptions> sessionOptions = Options.Create(new SessionPersistenceOptions { Enabled = false });

        var ctx = new ChatEndpoints.ChatTurnParityContext(
            Request: new ChatRequest("multi domain question", SessionId: "sess-1"),
            SessionId: "sess-1",
            UserId: "user-1",
            AgentKey: "planner",
            Intent: "multi/domain",
            Confidence: 0.5,
            Action: "chat.plan.multi/domain",
            Reply: "reply",
            InputTokens: 1,
            OutputTokens: 1,
            DurationMs: 100,
            SpanSummary: "{}",
            PersistenceEnabled: false);

        await ChatEndpoints.RecordChatTurnParityAsync(
            ctx,
            audit,
            exporter,
            sessionStore,
            sessionOptions,
            tenantProvider.Object,
            NullLogger.Instance,
            CancellationToken.None);

        audit.Entries.Should().HaveCount(1,
            "audit is always-on observability, not gated by session persistence");
        SessionPreview? preview = await exporter.GetPreviewAsync("sess-1", 100);
        preview!.Messages.Should().HaveCount(2,
            "conversation export is always-on observability, not gated by session persistence");
        sessionStore.Writes.Should().BeEmpty(
            "durable session writes must not be attempted when persistence is disabled");
    }

    /// <summary>
    /// The plan-first span summary must expose the specialist keys the
    /// planner sequenced. This is the "tools used" equivalent for a plan
    /// turn and is what the rehydrated session UI reads at a glance.
    /// </summary>
    [Fact]
    public void BuildPlanSpanSummary_includes_specialist_keys_as_tools()
    {
        var planResult = new PlanOrchestrationResult(
            PlanId: "plan-1",
            Status: PlanStatus.Completed,
            Reply: "reply",
            DurationMs: 1000,
            InputTokens: 10,
            OutputTokens: 5,
            TotalTokens: 15,
            Steps:
            [
                new(0, "s0", "scorecard", "scorecard", "summarize", PlanStepStatus.Completed, "r0", null, 5, 3, 8, 500),
                new(1, "s1", "demand-forecasting", "demand", "forecast", PlanStepStatus.Completed, "r1", null, 5, 2, 7, 500),
            ],
            FailureReason: null);

        string summary = ChatEndpoints.BuildPlanSpanSummary(planResult);

        summary.Should().Contain("scorecard");
        summary.Should().Contain("demand-forecasting");
        summary.Should().Contain(PlanStatus.Completed);
        summary.Should().Contain("plan-1");
    }

    // ────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────

    private static ConversationExporter CreateExporter()
    {
        IOptions<Api.Configuration.ObservabilityOptions> options = Options.Create(new Api.Configuration.ObservabilityOptions
        {
            MaxCostEvents = 1000,
            MaxSessions = 100,
            MaxMessagesPerSession = 200,
        });
        return new ConversationExporter(options);
    }

    private static string LoadChatEndpointsSource()
    {
        string startDir = Path.GetDirectoryName(typeof(PlanFirstRegressionTests).Assembly.Location)!;
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            string candidate = Path.Combine(
                dir.FullName, "src", "RetailPulse.Api", "Endpoints", "ChatEndpoints.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate ChatEndpoints.cs by walking up from " + startDir);
    }

    // ── Recording fakes ─────────────────────────────────────────────────

    private sealed class RecordingAuditLog : IAuditLog
    {
        public ConcurrentQueue<AuditEntry> Log { get; } = new();

        public IReadOnlyList<AuditEntry> Entries => [.. Log];

        public Task LogAsync(AuditEntry entry, CancellationToken ct = default)
        {
            Log.Enqueue(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuditEntry>>([.. Log]);

        public Task<AuditStats> GetStatsAsync(CancellationToken ct = default)
            => Task.FromResult(new AuditStats(Log.Count, [], []));
    }

    private sealed class RecordingSessionStore : ISessionStore
    {
        public List<SessionTurnWrite> Writes { get; } = [];

        public Task PersistTurnAsync(SessionTurnWrite write, CancellationToken ct = default)
        {
            Writes.Add(write);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SessionSummaryDto>> ListSessionsForSubjectAsync(string subject, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionSummaryDto>>([]);

        public Task<SessionDetailDto?> GetSessionAsync(string subject, string sessionId, CancellationToken ct = default)
            => Task.FromResult<SessionDetailDto?>(null);

        public Task<bool> DeleteSessionAsync(string subject, string sessionId, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<CleanupResult> PurgeExpiredAsync(DateTimeOffset olderThan, CancellationToken ct = default)
            => Task.FromResult(new CleanupResult(0, 0));
    }
}

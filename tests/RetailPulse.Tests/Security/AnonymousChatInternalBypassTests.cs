using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Moq;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Caching;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Endpoints;
using RetailPulse.Api.Guardrails;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Memory;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Models;
using RetailPulse.Api.Observability;
using RetailPulse.Api.Rag;
using RetailPulse.Api.Security;
using RetailPulse.Api.Security.Anonymous;
using RetailPulse.Api.Tracing;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Caching;
using RetailPulse.Contracts.Consensus;
using RetailPulse.Contracts.Guardrails;
using RetailPulse.Contracts.Memory;
using RetailPulse.Contracts.Observability;
using RetailPulse.Contracts.Rag;
using RetailPulse.Contracts.Routing;
using ChatRequest = RetailPulse.Contracts.ChatRequest;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Chat-internal bypass regression tests that drive the REAL
/// <see cref="ChatEndpoints.MapChatEndpoints"/> delegate end-to-end through the anonymous stack in
/// an in-process <see cref="TestServer"/>. Unlike tool-filter unit tests, these prove the two
/// bypasses that tool filtering can NEVER reach are closed at the endpoint:
///
/// <list type="bullet">
///   <item><b>Blocker 1 — memory management.</b> The real <see cref="MemoryManagementAgent"/> calls
///     <c>IConversationMemory.StoreAsync</c>/<c>ForgetAsync</c> DIRECTLY (no AI tools). A
///     <c>remember that ...</c> / <c>forget everything</c> prompt classified as
///     <see cref="AgentIntent.MemoryManagement"/> must be refused BEFORE the agent runs — proven by
///     a spy memory that records zero mutations and a specialist-invocation counter of zero.</item>
///   <item><b>Blocker 2 — consensus council.</b> A <c>council/health</c> prompt must be refused
///     BEFORE the council interception, so <see cref="IConsensusCouncil.ConveneAsync"/> is never
///     called — no council model calls, and no early-return that would skip the single accounted
///     budget/audit path.</item>
///   <item><b>Blocker 3 — truthful accounting.</b> An allowed single-topic chat reaches exactly one
///     accounted <c>AgentExecutionPipeline</c> path: the cost tracker and audit log each record the
///     turn once, with the specialist's real token totals. Refusals are deterministic (no model) and
///     record NO cost/audit — nothing is fabricated.</item>
/// </list>
/// Router, specialists, memory, council, cost and audit are faked at their boundaries so no Azure
/// credentials are needed, but the endpoint's own anonymous gating, refusal logic and accounting all
/// run exactly as in production.
/// </summary>
public sealed class AnonymousChatInternalBypassTests
{
    private const string Issuer = "retail-pulse-anonymous";
    private const string Audience = "retail-pulse-api";
    private const string SigningKeyText = "anon-chat-bypass-test-signing-key-0123456789";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes(SigningKeyText));

    // ── Blocker 1: memory management is refused, with zero memory mutation ──────

    [Theory]
    [InlineData("remember that our flagship store is in Seattle")]
    [InlineData("please remember this: I prefer weekly reports")]
    [InlineData("forget everything we discussed")]
    [InlineData("reset and start fresh")]
    public async Task Anonymous_MemoryManagementPrompt_IsRefused_WithZeroMemoryMutation(string prompt)
    {
        using BypassFixture fx = CreateServer();

        EchoResult result = await PostChat(fx, Token("anon-mem"), new ChatRequest(prompt, SessionId: "m-1"));

        result.Reply.Should().Be(AnonymousChatRestrictions.MemoryRefusalMessage,
            "an anonymous memory-management turn must return the standard safe refusal");

        // The direct-write agent must never run: no StoreAsync, no ForgetAsync, no rows mutated.
        fx.Memory.Verify(m => m.StoreAsync(It.IsAny<string>(), It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()), Times.Never,
            "the refusal must fire before the MemoryManagementAgent can store anything");
        fx.Memory.Verify(m => m.ForgetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "the refusal must fire before the MemoryManagementAgent can forget anything");
        fx.Memory.Verify(m => m.ForgetEntryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        fx.SpecialistInvocations.Should().Be(0, "no specialist (and therefore no model) runs for a refused turn");

        // A refusal is not billable: no cost and no audit entry are fabricated.
        fx.Cost.Verify(c => c.TrackUsageAsync(It.IsAny<UsageEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        fx.Audit.Verify(a => a.LogAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Blocker 2: council is refused, with zero council model calls ────────────

    [Theory]
    [InlineData("give me a portfolio health assessment for Contoso")]
    [InlineData("convene the council on overall brand health")]
    public async Task Anonymous_CouncilPrompt_IsRefused_WithZeroCouncilCalls(string prompt)
    {
        using BypassFixture fx = CreateServer();

        EchoResult result = await PostChat(fx, Token("anon-council"), new ChatRequest(prompt, SessionId: "c-1"));

        result.Reply.Should().Be(AnonymousChatRestrictions.CouncilRefusalMessage,
            "an anonymous portfolio-health turn must return the standard safe refusal");

        // The council interception must never convene — no fan-out of model calls.
        fx.Council.Verify(c => c.ConveneAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never,
            "the refusal must fire before the council interception");

        fx.SpecialistInvocations.Should().Be(0, "no specialist runs for a refused council turn");

        // The early-return council path is the one that historically skipped accounting; a refusal
        // must likewise not fabricate cost/audit.
        fx.Cost.Verify(c => c.TrackUsageAsync(It.IsAny<UsageEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        fx.Audit.Verify(a => a.LogAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Blocker 3: allowed chat reaches ONE accounted pipeline, truthfully ──────

    [Fact]
    public async Task Anonymous_AllowedChat_ReachesSinglePipeline_WithTruthfulBudgetAndAudit()
    {
        using BypassFixture fx = CreateServer();

        EchoResult result = await PostChat(fx, Token("anon-ok"), new ChatRequest(
            "How is demand trending for our top brand?", SessionId: "g-1"));

        result.Reply.Should().Be("anon-ok", "an allowed single-topic chat reaches the general specialist");

        fx.SpecialistInvocations.Should().Be(1, "exactly one accounted AgentExecutionPipeline path runs");

        // Neither the direct-write memory agent nor the council is on the allowed path.
        fx.Memory.Verify(m => m.StoreAsync(It.IsAny<string>(), It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()), Times.Never);
        fx.Memory.Verify(m => m.ForgetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        fx.Council.Verify(c => c.ConveneAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // Accounting is truthful: exactly one usage event (with the specialist's real tokens) and
        // exactly one audit entry — no more, no less.
        fx.Cost.Verify(c => c.TrackUsageAsync(
            It.Is<UsageEvent>(u => u.InputTokens == 1 && u.OutputTokens == 1), It.IsAny<CancellationToken>()), Times.Once,
            "the single model path records its real token usage against the budget");
        fx.Audit.Verify(a => a.LogAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()), Times.Once,
            "the allowed turn is audited exactly once");
    }

    [Fact]
    public async Task Anonymous_DeterministicKeywordPrompts_CannotReachMemoryOrCouncil()
    {
        using BypassFixture fx = CreateServer();

        // A crafted prompt cannot defeat the refusals: they fire on the router's own classification,
        // not on prompt text. Drive both restricted intents and one allowed intent in sequence and
        // prove the restricted collaborators are never touched.
        await PostChat(fx, Token("k-1"), new ChatRequest("remember that margins are thin", SessionId: "k-1"));
        await PostChat(fx, Token("k-2"), new ChatRequest("portfolio health council please", SessionId: "k-2"));
        await PostChat(fx, Token("k-3"), new ChatRequest("what is our promo plan?", SessionId: "k-3"));

        fx.Memory.Verify(m => m.StoreAsync(It.IsAny<string>(), It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()), Times.Never);
        fx.Memory.Verify(m => m.ForgetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        fx.Council.Verify(c => c.ConveneAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // Only the single allowed general turn is accounted.
        fx.SpecialistInvocations.Should().Be(1, "only the allowed general prompt reaches a specialist");
        fx.Cost.Verify(c => c.TrackUsageAsync(It.IsAny<UsageEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        fx.Audit.Verify(a => a.LogAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static async Task<EchoResult> PostChat(BypassFixture fx, string token, ChatRequest body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(body);
        HttpResponseMessage resp = await fx.Client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return await resp.Content.ReadFromJsonAsync<EchoResult>() ?? new();
    }

    private static string Token(string subject)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new("provider", "Anonymous"),
            new("roles", "RetailPulse.Anonymous"),
            new("scp", "chat_limited"),
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        };

        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.CreateToken(descriptor);
    }

    private static BypassFixture CreateServer()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "Anonymous",
                ["Anonymous:AllowHosted"] = "true",
                ["Anonymous:SigningKey"] = SigningKeyText,
                ["Anonymous:Issuer"] = Issuer,
                ["Anonymous:Audience"] = Audience,
                ["Anonymous:MaxRequestBytes"] = "16384",
                ["Anonymous:Chat:PerSubjectPerMinute"] = "100",
                ["Anonymous:Chat:PerIpPerMinute"] = "100",
                ["Anonymous:Limits:DailyMaxRequests"] = "500",
                ["Anonymous:Limits:DailyMaxTokens"] = "1000000",
                ["Anonymous:Limits:DailyMaxCostUsd"] = "100",
            })
            .Build();

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddConfiguration(config);
        builder.Services.AddLogging();
        builder.Services.AddRouting();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSignalR();

        builder.Services.AddProviderNeutralAuthentication(config, builder.Environment);
        builder.Services.AddSingleton<ISessionOwnershipRegistry, SessionOwnershipRegistry>();

        builder.Services.AddSingleton<InMemoryResponseCache>();
        builder.Services.AddSingleton<IResponseCache>(sp => sp.GetRequiredService<InMemoryResponseCache>());
        builder.Services.AddSingleton<InMemoryKnowledgeBase>();
        builder.Services.AddSingleton<IKnowledgeBase>(sp => sp.GetRequiredService<InMemoryKnowledgeBase>());
        builder.Services.AddSingleton<RagContextProvider>();
        builder.Services.AddSingleton<InMemorySuspiciousRequestLog>();
        builder.Services.AddSingleton<ISuspiciousRequestLog>(sp => sp.GetRequiredService<InMemorySuspiciousRequestLog>());
        builder.Services.AddSingleton<GuardrailsConfig>();
        builder.Services.AddScoped<GuardrailsMiddleware>();
        builder.Services.AddSingleton<InMemoryTraceCollector>();
        builder.Services.Configure<ObservabilityOptions>(_ => { });
        builder.Services.AddSingleton<ConversationExporter>();
        builder.Services.AddSingleton<MemoryExtractionChannel>();
        builder.Services.AddSingleton<MemoryExtractionService>();
        builder.Services.AddSingleton<ConversationMemoryMiddleware>();
        builder.Services.AddScoped<StreamingMiddleware>();
        builder.Services.AddScoped<StreamingProgressFeature>();

        builder.Services.AddSingleton(new Mock<IChatClient>().Object);

        // Spy memory: if the direct-write MemoryManagementAgent ever ran, these would be hit.
        var memory = new Mock<IConversationMemory>();
        memory.Setup(m => m.StoreAsync(It.IsAny<string>(), It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        memory.Setup(m => m.ForgetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        memory.Setup(m => m.RecallAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        builder.Services.AddSingleton(memory.Object);

        // Spy council: proves ConveneAsync is never reached for anonymous.
        var council = new Mock<IConsensusCouncil>();
        builder.Services.AddSingleton(council.Object);

        var cost = new Mock<ICostTracker>();
        cost.Setup(c => c.TrackUsageAsync(It.IsAny<UsageEvent>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        builder.Services.AddSingleton(cost.Object);

        var audit = new Mock<IAuditLog>();
        audit.Setup(a => a.LogAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        builder.Services.AddSingleton(audit.Object);

        var tenant = new Mock<ITenantProvider>();
        tenant.Setup(t => t.GetTenant()).Returns(new TenantConfiguration());
        builder.Services.AddSingleton(tenant.Object);

        var counter = new SpecialistCounter();
        builder.Services.AddSingleton(counter);
        builder.Services.AddSingleton<IAgentRouter, KeywordRouter>();

        // The REAL direct-write memory agent (so a bypass would produce a real StoreAsync/ForgetAsync)
        // and a general echo specialist for the allowed path.
        builder.Services.AddSingleton<ISpecialistAgent>(sp => new CountingMemoryAgent(
            sp.GetRequiredService<IConversationMemory>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<MemoryManagementAgent>(),
            sp.GetRequiredService<SpecialistCounter>()));
        builder.Services.AddSingleton<ISpecialistAgent>(sp => new EchoSpecialist(sp.GetRequiredService<SpecialistCounter>()));

        WebApplication app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<AnonymousGuardMiddleware>();
        app.MapChatEndpoints(new AgentDefinition { Name = "test" });

        app.StartAsync().GetAwaiter().GetResult();
        return new BypassFixture(app, counter, memory, council, cost, audit);
    }

    private sealed class EchoResult
    {
        public string? Reply { get; set; }
        public string? SessionId { get; set; }
    }

    private sealed class SpecialistCounter
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Increment() => Interlocked.Increment(ref _count);
    }

    /// <summary>Deterministic keyword router: memory / council / general by prompt content.</summary>
    private sealed class KeywordRouter : IAgentRouter
    {
        public Task<RoutingDecision> RouteAsync(
            string message,
            IReadOnlyList<ChatHistoryMessage>? conversationHistory,
            UserContext? user,
            string? tenantId,
            CancellationToken ct = default)
        {
            string lower = (message ?? string.Empty).ToLowerInvariant();
            RoutingDecision decision =
                lower.Contains("remember") || lower.Contains("forget") || lower.Contains("reset")
                    ? new RoutingDecision("memory-management", AgentIntent.MemoryManagement, 0.95, [AgentIntent.MemoryManagement])
                : lower.Contains("council") || lower.Contains("portfolio health") || lower.Contains("health")
                    ? new RoutingDecision("portfolio-health", AgentIntent.PortfolioHealth, 0.95, [AgentIntent.PortfolioHealth])
                : new RoutingDecision("general", AgentIntent.General, 0.9, [AgentIntent.General]);
            return Task.FromResult(decision);
        }
    }

    /// <summary>Real MemoryManagementAgent behaviour, but counts invocations so a bypass is visible.</summary>
    private sealed class CountingMemoryAgent : ISpecialistAgent
    {
        private readonly MemoryManagementAgent _inner;
        private readonly SpecialistCounter _counter;

        public CountingMemoryAgent(IConversationMemory memory, ILogger<MemoryManagementAgent> logger, SpecialistCounter counter)
        {
            _inner = new MemoryManagementAgent(memory, logger);
            _counter = counter;
        }

        public string Key => _inner.Key;
        public string DisplayName => _inner.DisplayName;
        public string Model => _inner.Model;
        public IReadOnlyList<string> SupportedIntents => _inner.SupportedIntents;

        public Task<ChatResponse> HandleAsync(ChatRequest request, CancellationToken ct = default)
        {
            _counter.Increment();
            return _inner.HandleAsync(request, ct);
        }
    }

    /// <summary>Echoes the resolved userId and records one accounted invocation.</summary>
    private sealed class EchoSpecialist : ISpecialistAgent
    {
        private readonly SpecialistCounter _counter;
        public EchoSpecialist(SpecialistCounter counter)
        {
            _counter = counter;
        }

        public string Key => "general";
        public string DisplayName => "General";
        public string Model => "gpt-test";
        public IReadOnlyList<string> SupportedIntents => [AgentIntent.General];

        public Task<ChatResponse> HandleAsync(ChatRequest request, CancellationToken ct = default)
        {
            _counter.Increment();
            string subject = request.User?.ObjectId ?? "(none)";
            return Task.FromResult(new ChatResponse(
                subject,
                request.SessionId ?? "s",
                [new AgentSpan("agent.general.response", "response", "done", 1, DateTimeOffset.UtcNow, request.SessionId)],
                Charts: null,
                TotalDurationMs: 1,
                TokenUsage: new TokenUsage(1, 1, 2)));
        }
    }

    private sealed class BypassFixture : IDisposable
    {
        private readonly WebApplication _app;
        private readonly SpecialistCounter _counter;

        public BypassFixture(
            WebApplication app,
            SpecialistCounter counter,
            Mock<IConversationMemory> memory,
            Mock<IConsensusCouncil> council,
            Mock<ICostTracker> cost,
            Mock<IAuditLog> audit)
        {
            _app = app;
            _counter = counter;
            Memory = memory;
            Council = council;
            Cost = cost;
            Audit = audit;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }
        public Mock<IConversationMemory> Memory { get; }
        public Mock<IConsensusCouncil> Council { get; }
        public Mock<ICostTracker> Cost { get; }
        public Mock<IAuditLog> Audit { get; }
        public int SpecialistInvocations => _counter.Count;

        public void Dispose()
        {
            Client.Dispose();
            _app.StopAsync().GetAwaiter().GetResult();
            ((IDisposable)_app).Dispose();
        }
    }
}

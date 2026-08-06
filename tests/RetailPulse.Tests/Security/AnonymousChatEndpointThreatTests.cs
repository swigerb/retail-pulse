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
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Moq;
using RetailPulse.Api.Agents;
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
using RetailPulse.Api.Validation;
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
/// Threat regression tests that drive the REAL <see cref="ChatEndpoints.MapChatEndpoints"/> delegate
/// (not a stand-in echo) end-to-end through the anonymous stack in an in-process
/// <see cref="TestServer"/>. Router, specialists, memory, cost/audit and the LLM are faked at their
/// boundaries so no Azure credentials are required, but the endpoint's own identity resolution,
/// cache/memory gating, session-ownership binding and validation all run as in production.
///
/// It proves, at the endpoint level:
/// <list type="bullet">
///   <item><b>Finding 2</b> — a request-body <c>objectId</c> is ignored; identity is the token
///     subject, and two sessions are isolated;</item>
///   <item><b>Finding 7</b> — the response cache is disabled for anonymous, so identical prompts
///     from two subjects execute independently and never share a reply.</item>
/// </list>
/// </summary>
public sealed class AnonymousChatEndpointThreatTests
{
    private const string Issuer = "retail-pulse-anonymous";
    private const string Audience = "retail-pulse-api";
    private const string SigningKeyText = "anon-chat-endpoint-test-signing-key-0123456789";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes(SigningKeyText));

    [Fact]
    public async Task BodyObjectId_IsIgnored_IdentityComesFromTokenSubject()
    {
        using ChatFixture fx = CreateServer();
        string token = Token("anon-real");

        // The body carries a spoofed objectId; the real endpoint must resolve identity from the
        // signed token subject and normalise request.User.ObjectId to it before the agent runs.
        EchoResult result = await PostChat(fx, token, new ChatRequest(
            "hello", SessionId: "sess-1", User: new UserContext("anon-attacker", "Mallory", "m@x")));

        result.Reply.Should().Be("anon-real", "identity must be the token subject, never the body objectId");
    }

    [Fact]
    public async Task TwoSessions_AreIsolated_EachSeesOnlyItsOwnSubject()
    {
        using ChatFixture fx = CreateServer();

        EchoResult a = await PostChat(fx, Token("anon-A"), new ChatRequest("hi", SessionId: "s-a"));
        EchoResult b = await PostChat(fx, Token("anon-B"), new ChatRequest("hi", SessionId: "s-b"));

        a.Reply.Should().Be("anon-A");
        b.Reply.Should().Be("anon-B");
        a.Reply.Should().NotBe(b.Reply);
    }

    [Fact]
    public async Task Cache_IsDisabled_ForAnonymous_IdenticalPromptsExecuteIndependently()
    {
        using ChatFixture fx = CreateServer();

        // A deterministic, cacheable prompt. If the cache were active (subject-blind key), the second
        // subject would receive the first subject's cached reply. With the cache disabled for
        // anonymous, the agent runs for BOTH and each sees only its own subject.
        const string prompt = "How many stores do we have?";
        CacheHelpers.IsCacheable(prompt).Should().BeTrue("the test relies on a cacheable prompt");

        EchoResult a = await PostChat(fx, Token("anon-1"), new ChatRequest(prompt, SessionId: "c-1"));
        EchoResult b = await PostChat(fx, Token("anon-2"), new ChatRequest(prompt, SessionId: "c-2"));

        a.Reply.Should().Be("anon-1");
        b.Reply.Should().Be("anon-2", "the second subject must not receive the first subject's cached reply");
        fx.AgentInvocations.Should().Be(2, "the cache is disabled for anonymous — both requests reach the agent");
    }

    [Fact]
    public async Task HistoryBounds_AreRejected_BeforeModel_With400()
    {
        using ChatFixture fx = CreateServer();
        var oversizedHistory = Enumerable.Range(0, ChatRequestValidator.MaxHistoryMessages + 5)
            .Select(i => new ChatHistoryMessage("user", $"m{i}"))
            .ToList();

        HttpResponseMessage resp = await PostChatRaw(fx, Token("anon-hist"),
            new ChatRequest("hi", SessionId: "h-1", History: oversizedHistory));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "history count over the bound is rejected pre-model");
        fx.AgentInvocations.Should().Be(0, "validation must fail closed before the agent runs");
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static async Task<HttpResponseMessage> PostChatRaw(ChatFixture fx, string token, ChatRequest body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(body);
        return await fx.Client.SendAsync(req);
    }

    private static async Task<EchoResult> PostChat(ChatFixture fx, string token, ChatRequest body)
    {
        HttpResponseMessage resp = await PostChatRaw(fx, token, body);
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

    private static ChatFixture CreateServer()
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

        // Full anonymous stack: scheme + constrained policy + guard services + IAnonymousChatPolicy.
        builder.Services.AddProviderNeutralAuthentication(config, builder.Environment);

        // Session-ownership registry (consulted by the endpoint + hubs).
        builder.Services.AddSingleton<ISessionOwnershipRegistry, SessionOwnershipRegistry>();

        // Real, in-memory pipeline collaborators.
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

        // Streaming collaborators are only needed so the (blocked-for-anonymous) /api/chat/stream
        // route can have its metadata inferred at startup; the route itself is never reachable here.
        builder.Services.AddScoped<StreamingMiddleware>();
        builder.Services.AddScoped<StreamingProgressFeature>();

        // Faked boundaries (LLM + memory are never reached for anonymous; cost/audit/tenant are no-ops).
        builder.Services.AddSingleton(new Mock<IChatClient>().Object);
        builder.Services.AddSingleton(new Mock<IConversationMemory>().Object);
        builder.Services.AddSingleton(new Mock<IConsensusCouncil>().Object);

        var cost = new Mock<ICostTracker>();
        cost.Setup(c => c.TrackUsageAsync(It.IsAny<UsageEvent>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        builder.Services.AddSingleton(cost.Object);

        var audit = new Mock<IAuditLog>();
        audit.Setup(a => a.LogAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        builder.Services.AddSingleton(audit.Object);

        var tenant = new Mock<ITenantProvider>();
        tenant.Setup(t => t.GetTenant()).Returns(new TenantConfiguration());
        builder.Services.AddSingleton(tenant.Object);

        var counter = new AgentCounter();
        builder.Services.AddSingleton(counter);
        builder.Services.AddSingleton<IAgentRouter, FakeRouter>();
        builder.Services.AddSingleton<ISpecialistAgent>(sp => new EchoSpecialist(sp.GetRequiredService<AgentCounter>()));

        WebApplication app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<AnonymousGuardMiddleware>();
        app.MapChatEndpoints(new AgentDefinition { Name = "test" });

        app.StartAsync().GetAwaiter().GetResult();
        return new ChatFixture(app, counter);
    }

    private sealed class EchoResult
    {
        public string? Reply { get; set; }
        public string? SessionId { get; set; }
    }

    private sealed class AgentCounter
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Increment() => Interlocked.Increment(ref _count);
    }

    /// <summary>Routes everything to the "general" specialist.</summary>
    private sealed class FakeRouter : IAgentRouter
    {
        public Task<RoutingDecision> RouteAsync(
            string message,
            IReadOnlyList<ChatHistoryMessage>? conversationHistory,
            UserContext? user,
            string? tenantId,
            CancellationToken ct = default) =>
            Task.FromResult(new RoutingDecision("general", AgentIntent.General, 0.9, [AgentIntent.General]));
    }

    /// <summary>Echoes the resolved userId (request.User.ObjectId, normalised by the endpoint).</summary>
    private sealed class EchoSpecialist : ISpecialistAgent
    {
        private readonly AgentCounter _counter;
        public EchoSpecialist(AgentCounter counter)
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

    private sealed class ChatFixture : IDisposable
    {
        private readonly WebApplication _app;
        private readonly AgentCounter _counter;

        public ChatFixture(WebApplication app, AgentCounter counter)
        {
            _app = app;
            _counter = counter;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public int AgentInvocations => _counter.Count;

        public void Dispose()
        {
            Client.Dispose();
            _app.StopAsync().GetAwaiter().GetResult();
            ((IDisposable)_app).Dispose();
        }
    }
}

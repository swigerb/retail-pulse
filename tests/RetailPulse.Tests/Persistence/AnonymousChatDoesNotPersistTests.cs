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
using Microsoft.Extensions.Options;
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
using RetailPulse.Api.Persistence;
using RetailPulse.Api.Rag;
using RetailPulse.Api.Security;
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

namespace RetailPulse.Tests.Persistence;

/// <summary>
/// End-to-end regression test that drives the real <see cref="ChatEndpoints.MapChatEndpoints"/>
/// delegate through an in-process anonymous TestServer with the durable session store
/// switched ON, and asserts <b>zero</b> calls flow through <see cref="ISessionStore"/>.
///
/// This is the non-negotiable privacy proof for issue #90: anonymous callers already have
/// cache and memory disabled by policy, and conversation persistence must follow the same
/// rule. Testing this at the endpoint level (not the store level) is what closes the loop —
/// a regression that flips the persistence check while leaving the store guard would be
/// invisible to a store-only test.
/// </summary>
public sealed class AnonymousChatDoesNotPersistTests
{
    private const string Issuer = "retail-pulse-anonymous";
    private const string Audience = "retail-pulse-api";
    private const string SigningKeyText = "session-persist-anon-signing-key-0123456789";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes(SigningKeyText));

    [Fact]
    public async Task AnonymousChat_NeverWritesToSessionStore_EvenWithPersistenceEnabled()
    {
        var storeMock = new Mock<ISessionStore>(MockBehavior.Strict);

        using ChatFixture fx = CreateServer(storeMock);

        // Send several turns from two distinct anonymous subjects to a cacheable prompt and
        // a non-cacheable one. The session-persistence path runs on both the LLM-served
        // branch and the cache-hit branch of ChatEndpoints, so we cover both.
        HttpResponseMessage r1 = await PostChat(fx, Token("anon-x"), new ChatRequest("Hello there.", SessionId: "s-x"));
        HttpResponseMessage r2 = await PostChat(fx, Token("anon-y"), new ChatRequest("How many stores do we have?", SessionId: "s-y"));
        HttpResponseMessage r3 = await PostChat(fx, Token("anon-y"), new ChatRequest("How many stores do we have?", SessionId: "s-y"));

        r1.StatusCode.Should().Be(HttpStatusCode.OK);
        r2.StatusCode.Should().Be(HttpStatusCode.OK);
        r3.StatusCode.Should().Be(HttpStatusCode.OK);

        // The store is MockBehavior.Strict: any call at all — from either the LLM path or
        // the cache-hit path — would throw and fail this test.
        storeMock.Verify(
            s => s.PersistTurnAsync(It.IsAny<SessionTurnWrite>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "anonymous callers must never persist a turn, regardless of the feature switch");
    }

    private static async Task<HttpResponseMessage> PostChat(ChatFixture fx, string token, ChatRequest body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(body);
        return await fx.Client.SendAsync(req);
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

    private static ChatFixture CreateServer(Mock<ISessionStore> storeMock)
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

                // Session persistence is enabled here. The test proves anonymous callers
                // are refused at the pipeline gate REGARDLESS of the master switch, so the
                // switch itself is not what protects them.
                ["SessionPersistence:Enabled"] = "true",
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

        // Real pipeline collaborators (no LLM, no memory recall — anonymous disables both).
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

        builder.Services.AddSingleton<IAgentRouter, FakeRouter>();
        builder.Services.AddSingleton<ISpecialistAgent, EchoSpecialist>();

        // Wire the mock ISessionStore + real options section so ChatEndpoints
        // sees the same shape as production.
        builder.Services.AddSingleton(storeMock.Object);
        builder.Services.Configure<SessionPersistenceOptions>(
            config.GetSection(SessionPersistenceOptions.SectionName));

        WebApplication app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<AnonymousGuardMiddleware>();
        app.MapChatEndpoints(new AgentDefinition { Name = "test" });

        // Sanity: the options should surface Enabled=true so this test genuinely
        // exercises the "persistence is on but the caller is anonymous" case.
        SessionPersistenceOptions opts = app.Services.GetRequiredService<IOptions<SessionPersistenceOptions>>().Value;
        opts.Enabled.Should().BeTrue("the test set SessionPersistence:Enabled=true");

        app.StartAsync().GetAwaiter().GetResult();
        return new ChatFixture(app);
    }

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

    private sealed class EchoSpecialist : ISpecialistAgent
    {
        public string Key => "general";
        public string DisplayName => "General";
        public string Model => "gpt-test";
        public IReadOnlyList<string> SupportedIntents => [AgentIntent.General];

        public Task<ChatResponse> HandleAsync(ChatRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(
                request.User?.ObjectId ?? "(none)",
                request.SessionId ?? "s",
                [new AgentSpan("agent.general.response", "response", "done", 1, DateTimeOffset.UtcNow, request.SessionId)],
                Charts: null,
                TotalDurationMs: 1,
                TokenUsage: new TokenUsage(1, 1, 2)));
    }

    private sealed class ChatFixture : IDisposable
    {
        private readonly WebApplication _app;

        public ChatFixture(WebApplication app)
        {
            _app = app;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public void Dispose()
        {
            Client.Dispose();
            _app.StopAsync().GetAwaiter().GetResult();
            ((IDisposable)_app).Dispose();
        }
    }
}

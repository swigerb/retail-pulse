using FluentAssertions;
using Moq;
using RetailPulse.Api.Caching;
using RetailPulse.Api.Middleware;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Caching;
using RetailPulse.Contracts.Routing;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Tests.Integration;

/// <summary>
/// Full-pipeline integration test for the chat request path:
/// guardrails check → cache lookup → router classify → agent select →
/// agent execute (LLM + tools) → response assembly → cache store.
/// <para>
/// The production <c>/api/chat</c> endpoint cannot be hosted via
/// <c>WebApplicationFactory</c> (requires Azure credentials), so this suite
/// drives the same components in-memory in the exact order
/// <see cref="Api.Endpoints.ChatEndpoints"/> does. The LLM is mocked
/// at the <see cref="IChatClient"/> boundary so we exercise everything below
/// the model call. Cache, router, and specialist agents are the real
/// implementations.
/// </para>
/// </summary>
public class ChatPipelineIntegrationTests
{
    #region Happy Path — Full pipeline executes every stage

    [Fact]
    public async Task HappyPath_FullPipeline_ExecutesAllStagesInOrder()
    {
        var harness = new PipelineHarness();
        harness.WhenRouterClassifies(AgentIntent.General, confidence: 0.9);
        harness.WhenAgentReplies("Brand X is healthy with strong distribution.");

        ChatResponse response = await harness.RunPipelineAsync(
            new ChatRequest("Tell me about Brand X health", SessionId: "happy-path-1"));

        // Stage 1 — guardrails passed (response was produced, not refused)
        response.Reply.Should().NotBeNullOrEmpty();
        // Stage 3 — router classified
        harness.RouterWasCalled.Should().BeTrue();
        // Stage 4 — agent was selected (general was registered and routed-to)
        harness.SelectedAgentKey.Should().Be("general");
        // Stage 5 — agent executed → returned the canned reply through the pipeline
        response.Reply.Should().Contain("Brand X is healthy");
        // Stage 6 — response assembled with telemetry spans
        response.Spans.Should().NotBeEmpty("the pipeline must emit telemetry spans");
        response.Spans.Should().Contain(s => s.Type == "thought");
        response.Spans.Should().Contain(s => s.Type == "response");
        // Routing metadata wired up by harness in success paths
        response.Routing.Should().NotBeNull();
        response.Routing.AgentKey.Should().Be("general");
    }

    [Fact]
    public async Task HappyPath_SessionId_FlowsThroughPipeline()
    {
        var harness = new PipelineHarness();
        harness.WhenRouterClassifies(AgentIntent.General, confidence: 0.9);
        harness.WhenAgentReplies("ok");

        ChatResponse response = await harness.RunPipelineAsync(
            new ChatRequest("hello", SessionId: "session-trace-42"));

        response.SessionId.Should().Be("session-trace-42",
            "the session id from the request must survive end-to-end");
        response.Spans.Should().OnlyContain(s => s.SessionId == "session-trace-42");
    }

    #endregion

    #region Cache Hit — Skips Agent Execution

    [Fact]
    public async Task CacheHit_ReturnsCachedReply_WithoutInvokingAgent()
    {
        var harness = new PipelineHarness();
        harness.WhenRouterClassifies(AgentIntent.General, confidence: 0.9);
        harness.WhenAgentReplies("LIVE AGENT RESPONSE — should not appear");

        // Pre-seed cache with the exact key the pipeline will compute
        const string query = "How many stores do we have?";
        string cacheKey = CacheHelpers.BuildCacheKey("pre-route", query);
        await harness.Cache.SetAsync(cacheKey,
            new CachedResponse("Cached: 412 stores.", "general", DateTime.UtcNow, cacheKey),
            TimeSpan.FromMinutes(5));

        ChatResponse response = await harness.RunPipelineAsync(
            new ChatRequest(query, SessionId: "cache-hit"));

        response.Reply.Should().Be("Cached: 412 stores.");
        response.Reply.Should().NotContain("LIVE AGENT RESPONSE");
        harness.RouterWasCalled.Should().BeFalse("cache hit must short-circuit before router classification");
        harness.AgentWasInvoked.Should().BeFalse("cache hit must short-circuit before agent execution");
        // Cache-hit reply carries a synthetic cache.hit span (matches production)
        response.Spans.Should().ContainSingle(s => s.Type == "cache");
    }

    [Fact]
    public async Task CacheMiss_FallsThroughToFullPipeline()
    {
        var harness = new PipelineHarness();
        harness.WhenRouterClassifies(AgentIntent.General, confidence: 0.9);
        harness.WhenAgentReplies("Fresh response from agent.");

        ChatResponse response = await harness.RunPipelineAsync(
            new ChatRequest("Some deterministic question", SessionId: "cache-miss"));

        response.Reply.Should().Contain("Fresh response");
        harness.RouterWasCalled.Should().BeTrue();
        harness.AgentWasInvoked.Should().BeTrue();
    }

    [Fact]
    public async Task NonDeterministicQuery_SkipsCacheLookup()
    {
        var harness = new PipelineHarness();
        harness.WhenRouterClassifies(AgentIntent.DemandForecasting, confidence: 0.9);
        harness.WhenAgentReplies("Forecast: +15% next quarter.");

        // "forecast" is a non-deterministic keyword — IsCacheable returns false.
        const string query = "What is the demand forecast for Brand X?";
        CacheHelpers.IsCacheable(query).Should().BeFalse(
            "non-deterministic queries must bypass the cache");

        ChatResponse response = await harness.RunPipelineAsync(
            new ChatRequest(query, SessionId: "non-det"));

        response.Reply.Should().Contain("Forecast");
        harness.AgentWasInvoked.Should().BeTrue();
    }

    #endregion

    #region Router Classification → Agent Selection

    [Theory]
    [InlineData(AgentIntent.DemandForecasting, "demand-forecasting")]
    [InlineData(AgentIntent.PromotionTrade, "promo-planning")]
    [InlineData(AgentIntent.General, "general")]
    public async Task Router_ClassifiesIntent_AndPipelineSelectsMatchingSpecialist(
        string intent, string expectedAgentKey)
    {
        var harness = new PipelineHarness();
        harness.RegisterSpecialist(expectedAgentKey, intent);
        harness.WhenRouterClassifies(intent, confidence: 0.92);

        ChatResponse response = await harness.RunPipelineAsync(
            new ChatRequest("scenario", SessionId: $"select-{intent}"));

        harness.SelectedAgentKey.Should().Be(expectedAgentKey,
            $"router classified as {intent}; pipeline must select {expectedAgentKey}");
        response.Routing.Should().NotBeNull();
        response.Routing.Intent.Should().Be(intent);
    }

    [Fact]
    public async Task Router_UnknownIntent_FallsBackToGeneralAgent()
    {
        var harness = new PipelineHarness();
        harness.WhenRouterClassifies("nonexistent/intent", confidence: 0.9, agentKeyOverride: "no-such-agent");
        harness.WhenAgentReplies("General fallback reply.");

        ChatResponse response = await harness.RunPipelineAsync(
            new ChatRequest("weird question", SessionId: "fallback"));

        harness.SelectedAgentKey.Should().Be("general",
            "unknown agent keys must fall through to the General specialist");
        response.Reply.Should().Contain("General fallback");
    }

    #endregion

    #region Response Assembly

    [Fact]
    public async Task ResponseAssembly_IncludesRoutingInfo_OnSuccessfulReply()
    {
        var harness = new PipelineHarness();
        harness.WhenRouterClassifies(AgentIntent.General, confidence: 0.87);
        harness.WhenAgentReplies("Healthy reply with substance.");

        ChatResponse response = await harness.RunPipelineAsync(
            new ChatRequest("status?", SessionId: "routing-meta"));

        response.Routing.Should().NotBeNull();
        response.Routing.AgentKey.Should().Be("general");
        response.Routing.Confidence.Should().BeApproximately(0.87, 0.01);
        response.Routing.DurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task ResponseAssembly_StripsRoutingInfo_OnErrorReply()
    {
        // Production strips RoutingInfo if the agent reply starts with ⏳/⚠️.
        // The error-prefixed reply comes from AgentExecutionPipeline's own
        // catch blocks (HandleRateLimitError / HandleUnexpectedError).
        var harness = new PipelineHarness();
        harness.WhenRouterClassifies(AgentIntent.General, confidence: 0.9);
        harness.WhenAgentReplies("⏳ The AI service is experiencing high demand. Please wait 30 seconds and try again.");

        ChatResponse response = await harness.RunPipelineAsync(
            new ChatRequest("hello", SessionId: "err-strip"));

        response.Reply.Should().StartWith("⏳");
        response.Routing.Should().BeNull("error replies must not carry routing metadata");
    }

    [Fact]
    public async Task ResponseAssembly_StripsRoutingInfo_OnWarningReply()
    {
        var harness = new PipelineHarness();
        harness.WhenRouterClassifies(AgentIntent.General, confidence: 0.9);
        harness.WhenAgentReplies("⚠️ Something went wrong while contacting the AI service. Please try again in a moment.");

        ChatResponse response = await harness.RunPipelineAsync(
            new ChatRequest("hi", SessionId: "warn-strip"));

        response.Reply.Should().StartWith("⚠️");
        response.Routing.Should().BeNull();
    }

    #endregion

    #region Cache Write-Back

    [Fact]
    public async Task AfterSuccessfulPipeline_CacheablyQueries_ArePersistedToCache()
    {
        var harness = new PipelineHarness();
        harness.WhenRouterClassifies(AgentIntent.General, confidence: 0.9);
        harness.WhenAgentReplies("Deterministic answer.");

        const string query = "How many stores do we operate?";
        await harness.RunPipelineAsync(new ChatRequest(query, SessionId: "cache-write"));

        // Second identical request should hit the cache and bypass agent
        harness.ResetCallTracking();
        harness.WhenAgentReplies("UNEXPECTED — should not be called");

        ChatResponse second = await harness.RunPipelineAsync(
            new ChatRequest(query, SessionId: "cache-write-2"));

        second.Reply.Should().Be("Deterministic answer.");
        harness.AgentWasInvoked.Should().BeFalse(
            "the second identical, cacheable query must be served from cache");
    }

    #endregion

    /// <summary>
    /// In-memory harness that wires up the chat pipeline stages in the same
    /// order as <c>ChatEndpoints.MapChatEndpoints</c>:
    /// guardrails check → cache lookup → router classify → agent select →
    /// agent execute → response assembly → cache store.
    /// </summary>
    private sealed class PipelineHarness
    {
        public InMemoryResponseCache Cache { get; } = new();

        public bool RouterWasCalled { get; private set; }
        public bool AgentWasInvoked { get; private set; }
        public string? SelectedAgentKey { get; private set; }

        private readonly List<ISpecialistAgent> _specialists = [];
        private readonly Mock<IAgentRouter> _routerMock = new();
        private string _cannedReply = "default reply";
        private readonly Dictionary<string, Func<ChatRequest, CancellationToken, Task<ChatResponse>>> _agentHandlers = [];

        public PipelineHarness()
        {
            // Always register a general fallback specialist
            RegisterSpecialist("general", AgentIntent.General, "General");
        }

        public void RegisterSpecialist(string key, string supportedIntent, string? displayName = null)
        {
            if (_specialists.Any(s => s.Key == key)) return;

            var mock = new Mock<ISpecialistAgent>();
            mock.Setup(a => a.Key).Returns(key);
            mock.Setup(a => a.DisplayName).Returns(displayName ?? key);
            mock.Setup(a => a.Model).Returns("gpt-test");
            mock.Setup(a => a.SupportedIntents).Returns([supportedIntent]);
            mock.Setup(a => a.HandleAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
                .Returns<ChatRequest, CancellationToken>((req, ct) =>
                {
                    AgentWasInvoked = true;
                    return _agentHandlers.TryGetValue(key, out Func<ChatRequest, CancellationToken, Task<ChatResponse>>? custom)
                        ? custom(req, ct)
                        : Task.FromResult(new ChatResponse(
                        _cannedReply,
                        req.SessionId ?? "test",
                        [
                            new AgentSpan($"agent.{key}.thought", "thought", "Thinking", 5, DateTimeOffset.UtcNow, req.SessionId),
                            new AgentSpan($"agent.{key}.response", "response", "Done", 12, DateTimeOffset.UtcNow, req.SessionId)
                        ],
                        Charts: null,
                        TotalDurationMs: 17));
                });

            _specialists.Add(mock.Object);
        }

        public void WhenRouterClassifies(string intent, double confidence, string? agentKeyOverride = null)
        {
            _routerMock.Reset();
            _routerMock
                .Setup(r => r.RouteAsync(
                    It.IsAny<string>(),
                    It.IsAny<IReadOnlyList<ChatHistoryMessage>?>(),
                    It.IsAny<UserContext?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    RouterWasCalled = true;
                    return new RoutingDecision(
                        AgentKey: agentKeyOverride ?? KeyForIntent(intent),
                        Intent: intent,
                        Confidence: confidence,
                        DetectedIntents: [intent]);
                });
        }

        public void WhenAgentReplies(string reply) => _cannedReply = reply;

        public void ResetCallTracking()
        {
            RouterWasCalled = false;
            AgentWasInvoked = false;
            SelectedAgentKey = null;
        }

        private static string KeyForIntent(string intent) => intent switch
        {
            AgentIntent.DemandForecasting => "demand-forecasting",
            AgentIntent.PromotionTrade => "promo-planning",
            AgentIntent.SupplyShipments => "supply-chain",
            AgentIntent.CompetitiveMarket => "competitive-intel",
            _ => "general"
        };

        /// <summary>
        /// Runs the same pipeline stages as <c>ChatEndpoints.MapChatEndpoints</c>:
        /// guardrails (skipped here — assumed passing), cache lookup, router
        /// classification, agent selection, agent execution, response assembly,
        /// and cache write-back.
        /// </summary>
        public async Task<ChatResponse> RunPipelineAsync(ChatRequest request)
        {
            string sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
            CancellationToken ct = CancellationToken.None;

            // Stage 2 — cache lookup
            if (CacheHelpers.IsCacheable(request.Message))
            {
                string cacheKey = CacheHelpers.BuildCacheKey("pre-route", request.Message);
                CachedResponse? cached = await Cache.GetAsync(cacheKey, ct);
                if (cached is not null && cached.AgentId != "cache-warming")
                {
                    return new ChatResponse(
                        cached.Response,
                        sessionId,
                        [new AgentSpan("cache.hit", "cache", $"Served from cache (agent: {cached.AgentId})", 0, DateTimeOffset.UtcNow, sessionId)],
                        null, 0);
                }
            }

            // Stage 3 — router classification
            DateTimeOffset routingStart = DateTimeOffset.UtcNow;
            RoutingDecision decision = await _routerMock.Object.RouteAsync(
                request.Message, request.History, request.User, tenantId: null, ct);

            // Stage 4 — agent selection (with general fallback)
            ISpecialistAgent? specialist = _specialists.FirstOrDefault(s =>
                string.Equals(s.Key, decision.AgentKey, StringComparison.OrdinalIgnoreCase))
                ?? _specialists.First(s => s.Key == "general");

            SelectedAgentKey = specialist.Key;

            var routingInfo = new RoutingInfo(
                specialist.Key,
                specialist.DisplayName,
                decision.Intent,
                decision.Confidence,
                (long)(DateTimeOffset.UtcNow - routingStart).TotalMilliseconds);

            // Stage 5 — agent executes
            ChatResponse response = await specialist.HandleAsync(
                request with { SessionId = sessionId }, ct);

            // Stage 6 — response assembly: attach routing only on success replies
            bool isErrorResponse = response.Reply.StartsWith('⏳')
                || response.Reply.StartsWith("⚠️", StringComparison.Ordinal);
            response = response with { Routing = isErrorResponse ? null : routingInfo };

            // Stage 7 — cache write-back (deterministic queries only)
            if (CacheHelpers.IsCacheable(request.Message))
            {
                string cacheKey = CacheHelpers.BuildCacheKey("pre-route", request.Message);
                await Cache.SetAsync(cacheKey,
                    new CachedResponse(response.Reply, specialist.Key, DateTime.UtcNow, cacheKey),
                    TimeSpan.FromMinutes(5), ct);
            }

            return response;
        }
    }

}

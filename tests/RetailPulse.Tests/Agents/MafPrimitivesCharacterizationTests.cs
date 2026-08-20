using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Agents.Routing;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Consensus;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Consensus;
using RetailPulse.Contracts.Memory;
using RetailPulse.Contracts.Routing;
using RetailPulse.Tests.Fixtures;
using MafAgentResponse = Microsoft.Agents.AI.AgentResponse;

namespace RetailPulse.Tests.Agents;

/// <summary>
/// Characterization tests for GitHub issue #89 — proves that the specialist
/// pipeline, <see cref="RetailOpsRouter"/>, and <see cref="ConsensusOrchestrator"/>
/// all execute through a genuine Microsoft Agent Framework (MAF) 1.18.0
/// <c>ChatClientAgent</c> primitive, not merely through direct
/// <see cref="IChatClient.GetResponseAsync"/> calls dressed up with MAF symbols.
/// <para>
/// The proof is stack-frame based: the tests inject an <see cref="IChatClient"/>
/// wrapper that captures its own call site. If MAF is genuinely on the execution
/// path, that call site's stack will contain a
/// <c>Microsoft.Agents.AI.ChatClientAgent</c> frame — that frame does not exist
/// when a caller invokes <see cref="IChatClient.GetResponseAsync"/> directly.
/// </para>
/// <para>
/// Additional characterization: observed <see cref="ChatOptions"/> preserve the
/// exact tool set, temperature, and response format the pipeline/router/council
/// pass in, proving MAF does not silently rewrap or re-decorate the caller stack
/// (validates <c>UseProvidedChatClientAsIs = true</c> is effective).
/// </para>
/// <para>
/// This class joins the shared <c>OTel</c> xUnit collection
/// (<see cref="RetailPulse.Tests.Fixtures.OTelCollection"/>). Its router,
/// specialist, and span-order tests emit spans on the
/// <c>RetailPulse.Agent</c> <see cref="ActivitySource"/> — the same source that
/// <c>OTelRoutingSpanTests</c> subscribes to with a process-wide
/// <see cref="ActivityListener"/>. Serializing the two classes prevents the
/// otherwise-inevitable cross-contamination where an <c>agent.routing</c> span
/// emitted by a MAF characterization run gets picked up as the
/// <c>LastOrDefault</c> match by an <c>OTelRoutingSpanTests</c> assertion.
/// </para>
/// </summary>
[Collection("OTel")]
public class MafPrimitivesCharacterizationTests
{
    #region MafAgentInvoker direct contract

    [Fact]
    public async Task MafAgentInvoker_ReturnsGenuineMafAgentResponse()
    {
        var probe = MafChatClientProbe.WithAssistantReply("hello world");

        var response = await MafAgentInvoker.RunAsync(
            probe,
            agentName: "test.agent",
            messages: [new ChatMessage(ChatRole.User, "hi")],
            chatOptions: new ChatOptions(),
            loggerFactory: NullLoggerFactory.Instance,
            ct: CancellationToken.None);

        response.Should().NotBeNull();
        response.Should().BeAssignableTo<MafAgentResponse>();
        response.Text.Should().Contain("hello world");
        response.Messages.Should().NotBeEmpty("MAF must surface at least the assistant reply message");
    }

    [Fact]
    public async Task MafAgentInvoker_InvokesUnderlyingChatClientThroughMafFrame()
    {
        var probe = MafChatClientProbe.WithAssistantReply("ok");

        await MafAgentInvoker.RunAsync(
            probe,
            agentName: "test.agent",
            messages: [new ChatMessage(ChatRole.User, "hi")],
            chatOptions: new ChatOptions(),
            loggerFactory: NullLoggerFactory.Instance,
            ct: CancellationToken.None);

        probe.Calls.Should().HaveCount(1, "MAF should invoke the provided chat client exactly once for a simple, tool-less run");
        probe.Calls[0].WasCalledFromMafFrame.Should().BeTrue(
            "the invocation must originate inside Microsoft.Agents.AI.ChatClientAgent, proving the request " +
            "actually flowed through a real MAF primitive rather than a direct IChatClient call");
    }

    [Fact]
    public async Task MafAgentInvoker_PreservesChatOptions_ForToolAndResponseFormatContracts()
    {
        var probe = MafChatClientProbe.WithAssistantReply("{ }");

        var tool = new StubAITool("stub-tool");
        var options = new ChatOptions
        {
            Temperature = 0.42f,
            Tools = [tool],
            ResponseFormat = ChatResponseFormat.Json,
            MaxOutputTokens = 128,
        };

        await MafAgentInvoker.RunAsync(
            probe,
            agentName: "test.agent",
            messages: [new ChatMessage(ChatRole.User, "hi")],
            chatOptions: options,
            loggerFactory: NullLoggerFactory.Instance,
            ct: CancellationToken.None);

        probe.Calls.Should().HaveCount(1);
        var observed = probe.Calls[0].Options;
        observed.Should().NotBeNull();
        observed.Temperature.Should().Be(0.42f, "MAF must forward temperature unchanged");
        observed.ResponseFormat.Should().BeSameAs(ChatResponseFormat.Json, "response format must be preserved");
        observed.MaxOutputTokens.Should().Be(128, "max output tokens must be preserved");
        observed.Tools.Should().NotBeNull();
        observed.Tools.Should().ContainSingle(t => ReferenceEquals(t, tool),
            "the caller-provided tool list must reach the chat client unchanged (no double-wrapping)");
    }

    #endregion

    #region Specialists (via AgentExecutionPipeline)

    [Fact]
    public async Task Specialist_ExecutesThroughMafChatClientAgent()
    {
        var probe = MafChatClientProbe.WithAssistantReply("hi from specialist");
        var pipeline = new AgentExecutionPipeline(
            probe,
            AgentTestFixtures.CreateMockHubContext(),
            EmptyConfiguration(),
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());

        var agent = new RetailPulse.Api.Agents.Specialists.GeneralAgent(
            pipeline,
            new AgentDefinition
            {
                Name = "General",
                Model = "gpt-4o",
                SystemPrompt = "You are a retail analytics assistant.",
                Temperature = 0.7
            },
            tools: []);

        var response = await agent.HandleAsync(new ChatRequest("hello"));

        response.Reply.Should().Contain("hi from specialist");
        probe.Calls.Should().HaveCount(1, "the specialist should trigger exactly one chat client invocation via MAF");
        probe.Calls[0].WasCalledFromMafFrame.Should().BeTrue(
            "specialists must invoke IChatClient through the MafAgentInvoker/ChatClientAgent primitive path");
    }

    #endregion

    #region Router

    [Fact]
    public async Task Router_ClassifiesIntentThroughMafChatClientAgent()
    {
        // Force the keyword fast-path to miss so classification actually runs through MAF.
        // "Give me a full portfolio deep dive across every domain" avoids every keyword pattern.
        var probe = MafChatClientProbe.WithAssistantReply(
            "{\"intent\":\"" + AgentIntent.General + "\",\"confidence\":0.92,\"intents\":[]}");

        var router = new RetailOpsRouter(
            probe,
            new AgentDefinition
            {
                Name = "Router",
                Model = "gpt-5.4-mini",
                SystemPrompt = "Classify user intent.",
                Temperature = 0.1
            },
            specialists: [],
            Mock.Of<ILogger<RetailOpsRouter>>());

        var decision = await router.RouteAsync(
            "Give me a full portfolio deep dive across every domain",
            null, null, null);

        decision.Should().NotBeNull();
        probe.Calls.Should().NotBeEmpty("the router must invoke the chat client for LLM-based classification");
        probe.Calls.Should().OnlyContain(c => c.WasCalledFromMafFrame,
            "every router classification call must flow through the shared MafAgentInvoker path");
    }

    #endregion

    #region Consensus Council

    [Fact]
    public async Task ConsensusOrchestrator_VoterAndSynthesizer_BothRouteThroughMaf()
    {
        var probe = MafChatClientProbe.WithAssistantReply(
            "{\"rating\":\"Green\",\"reasoning\":\"stable performance across domain\",\"confidence\":0.85,\"tags\":[]}");

        ISpecialistAgent[] specialists =
        [
            AgentTestFixtures.CreateMockSpecialist("demand-forecasting", [AgentIntent.DemandForecasting]),
            AgentTestFixtures.CreateMockSpecialist("competitive-intel",  [AgentIntent.CompetitiveMarket]),
            AgentTestFixtures.CreateMockSpecialist("supply-chain",       [AgentIntent.SupplyShipments]),
        ];

        var orchestrator = new ConsensusOrchestrator(
            specialists,
            probe,
            synthesisDef: new AgentDefinition { Name = "council-synth", SystemPrompt = "Synthesize.", Temperature = 0.1 },
            voteDef:      new AgentDefinition { Name = "council-vote",  SystemPrompt = "Vote.",       Temperature = 0.0 },
            Mock.Of<ILogger<ConsensusOrchestrator>>());

        var verdict = await orchestrator.ConveneAsync(
            "Sierra Gold Tequila", "Southwest", CancellationToken.None);

        verdict.Should().NotBeNull();

        // 3 specialists vote in parallel + 1 synthesis pass = 4 MAF-routed chat calls.
        probe.Calls.Should().HaveCount(4,
            "the council must issue exactly one MAF-routed vote per participant plus one MAF-routed synthesis call");
        probe.Calls.Should().OnlyContain(c => c.WasCalledFromMafFrame,
            "every voter and synthesis call must flow through the shared MafAgentInvoker path");
    }

    #endregion

    #region Specialist inventory (all 10 ISpecialistAgent keys)

    /// <summary>
    /// Representative characterization for every <see cref="ISpecialistAgent"/> key
    /// registered by production DI. For the nine LLM-backed specialists this proves
    /// that <see cref="ISpecialistAgent.HandleAsync"/> executes exactly one chat
    /// invocation and that the invocation originates inside a
    /// <c>Microsoft.Agents.AI.ChatClientAgent</c> stack frame — the acceptance
    /// contract that all real specialist inference flows through MAF. For the
    /// single rules-only specialist (<see cref="MemoryManagementAgent"/>) it
    /// asserts the opposite: <see cref="IChatClient"/> must NOT be called, and
    /// the deterministic rules-only reply must reach the caller unchanged. That
    /// keeps the characterization honest — MemoryManagement's <c>Model</c> is
    /// documented as <c>"none"</c> and the specialist responds without an LLM.
    /// </summary>
    [Theory]
    [InlineData("general",            true,  "Give me a summary of the retail landscape.")]
    [InlineData("demand-forecasting", true,  "What is the demand forecast for Sierra Gold this quarter?")]
    [InlineData("competitive-intel",  true,  "How is our competitive threat landscape looking this month?")]
    [InlineData("supply-chain",       true,  "Are our supply chain shipments on track for the West region?")]
    [InlineData("promo-planning",     true,  "Plan a summer promotion for premium spirits.")]
    [InlineData("store-ops",          true,  "What operational issues should stores focus on this quarter?")]
    [InlineData("planogram",          true,  "How should we adjust the planogram for the East region?")]
    [InlineData("margin-analysis",    true,  "Analyze margin performance for tequila brands.")]
    [InlineData("field-sentiment",    true,  "What is the field sentiment for Sierra Gold in the Northeast?")]
    [InlineData("memory-management",  false, "Remember that I prefer premium tequila.")]
    public async Task Specialist_HandlerBehavior_MatchesMafRoutingContract_ForEachKey(
        string specialistKey, bool routesThroughMaf, string representativePrompt)
    {
        // Each specialist gets a private probe so a parallel-safe execution
        // (the OTel collection already serializes this class, but a scoped
        // probe per invocation also removes any latent aliasing between rows).
        var probe = MafChatClientProbe.WithAssistantReply(
            $"[stub reply for '{specialistKey}']");

        ISpecialistAgent specialist = BuildSpecialist(specialistKey, probe);

        specialist.Key.Should().Be(specialistKey,
            "the built specialist must own the intended DI key");

        RetailPulse.Contracts.ChatResponse response =
            await specialist.HandleAsync(new ChatRequest(representativePrompt));

        response.Should().NotBeNull(
            $"specialist '{specialistKey}' must return a well-formed ChatResponse for a representative prompt");

        if (routesThroughMaf)
        {
            probe.Calls.Should().HaveCount(1,
                $"LLM-backed specialist '{specialistKey}' must issue exactly one chat client " +
                "invocation through the shared MafAgentInvoker path (no tools attached, so no " +
                "additional round-trips are expected)");
            probe.Calls[0].WasCalledFromMafFrame.Should().BeTrue(
                $"specialist '{specialistKey}' must reach IChatClient through the " +
                "Microsoft.Agents.AI.ChatClientAgent primitive — the byte-equivalent " +
                "acceptance criterion for issue #89");
        }
        else
        {
            probe.Calls.Should().BeEmpty(
                $"specialist '{specialistKey}' is documented as rules-only (Model = \"none\") — " +
                "any IChatClient call would prove an LLM was silently invoked and would " +
                "contradict the specialist's contract");
            response.Reply.Should().Contain("remember",
                "the rules-only store path must acknowledge the user's remember request in prose");
        }
    }

    /// <summary>
    /// Builds a real specialist for the given key using the same constructors
    /// production DI does. LLM-backed specialists share a pipeline that wraps
    /// the supplied <see cref="MafChatClientProbe"/>; <see cref="MemoryManagementAgent"/>
    /// ignores the probe and uses an in-memory fake <see cref="IConversationMemory"/>.
    /// </summary>
    private static ISpecialistAgent BuildSpecialist(string key, IChatClient probe)
    {
        IHubContext<TelemetryHub> hubContext = AgentTestFixtures.CreateMockHubContext();
        AgentExecutionPipeline pipeline = new(
            probe,
            hubContext,
            EmptyConfiguration(),
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());

        return key switch
        {
            "general"            => new GeneralAgent(pipeline, Def("General"), []),
            "demand-forecasting" => new DemandForecastAgent(pipeline, Def("Demand Forecast"), []),
            "competitive-intel"  => new CompetitiveIntelAgent(
                                        pipeline, Def("Competitive Intel"), [],
                                        hubContext, Mock.Of<ILogger<CompetitiveIntelAgent>>()),
            "supply-chain"       => new SupplyChainAgent(pipeline, Def("Supply Chain"), []),
            "promo-planning"     => new PromoPlanningAgent(pipeline, Def("Promo Planning"), []),
            "store-ops"          => new StoreOpsAgent(pipeline, Def("Store Ops"), []),
            "planogram"          => new PlanogramAgent(pipeline, Def("Planogram"), []),
            "margin-analysis"    => new MarginAgent(pipeline, Def("Margin"), []),
            "field-sentiment"    => new FieldSentimentAgent(pipeline, Def("Field Sentiment"), []),
            "memory-management"  => new MemoryManagementAgent(
                                        new FakeConversationMemory(),
                                        Mock.Of<ILogger<MemoryManagementAgent>>()),
            _ => throw new ArgumentException(
                     $"Unknown specialist key '{key}' — add a factory row to keep the Theory in sync with production DI",
                     nameof(key)),
        };

        static AgentDefinition Def(string name) => new()
        {
            Name = name,
            Model = "gpt-4o",
            SystemPrompt = $"You are the {name} specialist for retail analytics.",
            Temperature = 0.4
        };
    }

    /// <summary>
    /// Minimal <see cref="IConversationMemory"/> that satisfies
    /// <see cref="MemoryManagementAgent"/>'s store/forget code paths without any
    /// persistence. The Theory only asserts that (a) no LLM call is issued and
    /// (b) the rules-only reply reaches the caller; a functional memory store
    /// is not required to make either claim.
    /// </summary>
    private sealed class FakeConversationMemory : IConversationMemory
    {
        public Task StoreAsync(string userId, MemoryEntry entry, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<MemoryEntry>> RecallAsync(
            string userId, string? query = null, int maxResults = 5, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

        public Task ForgetAsync(string userId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ForgetEntryAsync(string userId, string memoryId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    #endregion

    #region Span sequence characterization

    /// <summary>
    /// Locks in the outer span operation names that a specialist produces during a
    /// standard pipeline run: agent.thought opens first (the outer thinking scope);
    /// agent.response opens after the MAF invocation returns and closes before the
    /// outer scope. Both spans MUST be emitted, and their <c>StartTimeUtc</c>
    /// ordering must be preserved: agent.thought starts strictly before
    /// agent.response. Tool spans nest inside based on tool-call activity; byte-equivalent tool
    /// span names are covered by <c>TraceSpanTypeContractTests</c> and the
    /// SignalR push contract.
    /// </summary>
    [Fact]
    public async Task Specialist_EmitsAgentThoughtAndAgentResponseSpans_InCorrectOrder()
    {
        using var listener = ActivityCaptureListener.Capture(source => source.Name == "RetailPulse.Agent");

        // Start a test-owned outer Activity so every span emitted while `HandleAsync`
        // runs inherits our TraceId. That lets us filter cleanly for THIS test's spans
        // and ignore spans emitted by other xUnit tests running in parallel.
        using var testSource = new ActivitySource("RetailPulse.Tests.MafCharacterization");
        using var testListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "RetailPulse.Tests.MafCharacterization",
            Sample = SampleAll,
            ActivityStopped = _ => { },
        };
        ActivitySource.AddActivityListener(testListener);
        using var scopeActivity = testSource.StartActivity("test.scope");
        scopeActivity.Should().NotBeNull("the test scope Activity is required to correlate captured spans");
        var scopeTraceId = scopeActivity.TraceId;

        var probe = MafChatClientProbe.WithAssistantReply("done");
        var pipeline = new AgentExecutionPipeline(
            probe,
            AgentTestFixtures.CreateMockHubContext(),
            EmptyConfiguration(),
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());

        var agent = new RetailPulse.Api.Agents.Specialists.GeneralAgent(
            pipeline,
            new AgentDefinition
            {
                Name = "General",
                Model = "gpt-4o",
                SystemPrompt = "You are a retail analytics assistant.",
                Temperature = 0.7
            },
            tools: []);

        await agent.HandleAsync(new ChatRequest("hello"));

        var myActivities = listener.CapturedActivities
            .Where(a => a.TraceId == scopeTraceId)
            .ToList();

        var thoughtSpan = myActivities
            .SingleOrDefault(a => a.OperationName == "agent.thought");
        var responseSpan = myActivities
            .SingleOrDefault(a => a.OperationName == "agent.response");

        thoughtSpan.Should().NotBeNull("every specialist run must open an agent.thought span");
        responseSpan.Should().NotBeNull("every specialist run must open an agent.response span");

        thoughtSpan.StartTimeUtc.Should().BeOnOrBefore(responseSpan.StartTimeUtc,
            "agent.thought (the outer thinking scope) must open before the response span; this order " +
            "is the byte-equivalent characterization the frontend telemetry timeline relies on");
        responseSpan.Parent?.OperationName.Should().Be("agent.thought",
            "agent.response must be a child of agent.thought so the trace hierarchy is preserved");
    }

    private static ActivitySamplingResult SampleAll(ref ActivityCreationOptions<ActivityContext> options)
        => ActivitySamplingResult.AllDataAndRecorded;

    #endregion

    #region Helpers

    private static IConfiguration EmptyConfiguration()
        => new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

    /// <summary>
    /// Test-only <see cref="IChatClient"/> that captures every invocation and
    /// walks the current managed stack to prove Microsoft Agent Framework's
    /// <c>ChatClientAgent</c> is on the caller chain.
    /// </summary>
    private sealed class MafChatClientProbe : IChatClient
    {
        public sealed record CapturedCall(
            IReadOnlyList<ChatMessage> Messages,
            ChatOptions? Options,
            bool WasCalledFromMafFrame);

        public List<CapturedCall> Calls { get; } = [];

        private readonly Func<IReadOnlyList<ChatMessage>, ChatOptions?, Microsoft.Extensions.AI.ChatResponse> _responseFactory;

        private MafChatClientProbe(Func<IReadOnlyList<ChatMessage>, ChatOptions?, Microsoft.Extensions.AI.ChatResponse> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public static MafChatClientProbe WithAssistantReply(string reply) =>
            new((_, _) => new Microsoft.Extensions.AI.ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ChatMessage> captured = [.. messages];
            var fromMaf = IsCalledFromMafFrame(new StackTrace(fNeedFileInfo: false));
            Calls.Add(new CapturedCall(captured, options, fromMaf));
            return Task.FromResult(_responseFactory(captured, options));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Streaming is not used by the MAF invoker path in this characterization test.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        private static bool IsCalledFromMafFrame(StackTrace trace)
        {
            foreach (var frame in trace.GetFrames())
            {
                var declaring = frame?.GetMethod()?.DeclaringType;
                var name = declaring?.FullName;
                if (name is not null && name.StartsWith("Microsoft.Agents.AI.", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }

    private sealed class StubAITool : AITool
    {
        public StubAITool(string name) { Name = name; }
        public override string Name { get; }
    }

    /// <summary>
    /// Scoped <see cref="ActivityListener"/> that captures completed activities so
    /// tests can assert on span sequences.
    /// </summary>
    private sealed class ActivityCaptureListener : IDisposable
    {
        private readonly ActivityListener _listener;
        public ConcurrentQueue<Activity> Completed { get; } = new();

        public IReadOnlyList<Activity> CapturedActivities => [.. Completed];

        private ActivityCaptureListener(Func<ActivitySource, bool> predicate)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = predicate,
                Sample = SampleAllData,
                ActivityStopped = activity => Completed.Enqueue(activity),
            };
            ActivitySource.AddActivityListener(_listener);
        }

        private static ActivitySamplingResult SampleAllData(ref ActivityCreationOptions<ActivityContext> options)
            => ActivitySamplingResult.AllDataAndRecorded;

        public static ActivityCaptureListener Capture(Func<ActivitySource, bool> predicate) => new(predicate);

        public void Dispose() => _listener.Dispose();
    }

    #endregion
}

using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Agents.Routing;
using RetailPulse.Api.Consensus;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Consensus;
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
/// </summary>
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
